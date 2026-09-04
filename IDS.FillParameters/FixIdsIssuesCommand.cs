using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OfficeOpenXml;
using RevitLogger;

namespace IDS
{
    #region Models

    public class IdsIssue
    {
        public string Requirement { get; set; }
        public string Problem { get; set; }
        public string IfcClass { get; set; }
        public string PredefinedType { get; set; }
        public string Name { get; set; }
        public string Tag { get; set; }
        public string GlobalId { get; set; }

        public string PropertyName { get; set; }
        public string DataSet { get; set; }
        public RestrictionType RestrictionType { get; set; }
        public List<string> AllowedValues { get; set; } = new List<string>();
        public string Pattern { get; set; }
        public int? MinLength { get; set; }
    }

    public enum RestrictionType
    {
        None,
        Enumeration,
        Pattern,
        MinLength
    }

    public class MappingEntry
    {
        public string PropertySet { get; set; }
        public string IfcPropertyName { get; set; }
        public string DataType { get; set; }
        public string RevitParameterName { get; set; }
        public List<string> IfcClasses { get; set; } = new List<string>();
    }

    public class ElementIssues
    {
        public string Tag { get; set; }
        public int ElementId { get; set; }
        public string GlobalId { get; set; }       // Добавлено
        public bool IsGuidSearch { get; set; }      // Добавлено
        public List<IdsIssue> Issues { get; set; } = new List<IdsIssue>();
    }

    #endregion

    #region Mapping Parser

    public class MappingParser
    {
        public Dictionary<string, MappingEntry> ParseMapping(string filePath)
        {
            Logger.Info($"[MappingParser] Начало парсинга файла маппинга: {filePath}");

            if (!File.Exists(filePath))
            {
                Logger.Error($"[MappingParser] Файл маппинга не найден: {filePath}");
                return new Dictionary<string, MappingEntry>();
            }

            var mapping = new Dictionary<string, MappingEntry>(StringComparer.OrdinalIgnoreCase);
            var lines = File.ReadAllLines(filePath);

            Logger.Info($"[MappingParser] Прочитано строк в файле: {lines.Length}");

            string currentPropertySet = "";
            List<string> currentIfcClasses = new List<string>();
            int lineCount = 0;
            int mappingLines = 0;

            foreach (var rawLine in lines)
            {
                lineCount++;
                var line = rawLine;

                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                    continue;

                if (line.StartsWith("PropertySet:"))
                {
                    var parts = line.Split('\t');
                    Logger.Debug($"[MappingParser] Строка {lineCount}: PropertySet line, parts count: {parts.Length}");

                    if (parts.Length >= 4)
                    {
                        currentPropertySet = parts[1].Trim();
                        var classesStr = parts.Length > 3 ? parts[3] : "";
                        currentIfcClasses = classesStr.Split(',')
                            .Select(c => c.Trim())
                            .Where(c => !string.IsNullOrEmpty(c))
                            .ToList();

                        Logger.Debug($"[MappingParser] PropertySet: {currentPropertySet}, Classes: {string.Join(", ", currentIfcClasses)}");
                    }
                    else
                    {
                        Logger.Warning($"[MappingParser] Строка {lineCount}: PropertySet line имеет меньше 4 частей: {parts.Length}");
                    }
                    continue;
                }

                var mappingParts = line.Split('\t');
                var nonEmptyParts = mappingParts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();

                if (nonEmptyParts.Length >= 3)
                {
                    mappingLines++;
                    var ifcPropName = nonEmptyParts[0].Trim();
                    var dataType = nonEmptyParts[1].Trim();
                    var revitParamName = nonEmptyParts[2].Trim();

                    Logger.Debug($"[MappingParser] Строка {lineCount}: Mapping line - '{ifcPropName}' ({dataType}) → '{revitParamName}'");

                    if (!string.IsNullOrEmpty(ifcPropName) && !string.IsNullOrEmpty(revitParamName))
                    {
                        if (!mapping.ContainsKey(ifcPropName))
                        {
                            var entry = new MappingEntry
                            {
                                PropertySet = currentPropertySet,
                                IfcPropertyName = ifcPropName,
                                DataType = dataType,
                                RevitParameterName = revitParamName,
                                IfcClasses = new List<string>(currentIfcClasses)
                            };
                            mapping[ifcPropName] = entry;
                            Logger.Debug($"[MappingParser] Добавлен маппинг: {ifcPropName} ({dataType}) → {revitParamName}");
                        }
                    }
                }
                else
                {
                    Logger.Warning($"[MappingParser] Строка {lineCount}: Пропущена, nonEmptyParts count: {nonEmptyParts.Length}");
                }
            }

            Logger.Info($"[MappingParser] Итого загружено маппингов: {mapping.Count}");
            return mapping;
        }
    }

    #endregion

    #region ODS Parser

    public class OdsParser
    {
        private readonly XNamespace _tableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        private readonly XNamespace _textNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

        public List<IdsIssue> Parse(string filePath)
        {
            Logger.Info($"[OdsParser] Начало парсинга ODS файла: {filePath}");
            var issues = new List<IdsIssue>();

            using (var archive = ZipFile.OpenRead(filePath))
            {
                var contentEntry = archive.GetEntry("content.xml");
                if (contentEntry == null)
                {
                    Logger.Error("[OdsParser] Файл content.xml не найден в ODS архиве");
                    return issues;
                }

                XDocument doc;
                using (var stream = contentEntry.Open())
                {
                    doc = XDocument.Load(stream);
                }

                var tables = doc.Descendants(_tableNs + "table").ToList();
                for (int i = 1; i < tables.Count; i++)
                {
                    issues.AddRange(ParseTable(tables[i]));
                }
            }

            Logger.Info($"[OdsParser] Загружено проблем: {issues.Count}");
            return issues;
        }

        private List<IdsIssue> ParseTable(XElement table)
        {
            var issues = new List<IdsIssue>();
            var rows = table.Descendants(_tableNs + "table-row").ToList();

            Logger.Debug($"[OdsParser] Таблица содержит {rows.Count} строк");

            if (rows.Count < 2) return issues;

            // Первая строка — заголовок
            var headerCells = GetRowCells(rows[0]);
            Logger.Debug($"[OdsParser] Заголовок: {string.Join(" | ", headerCells)}");

            for (int i = 1; i < rows.Count; i++)
            {
                var cells = GetRowCells(rows[i]);

                Logger.Debug($"[OdsParser] Строка {i}: ячеек={cells.Count}");
                if (cells.Count > 0)
                {
                    Logger.Debug($"[OdsParser]   Requirement: {cells[0].Substring(0, Math.Min(50, cells[0].Length))}...");
                }

                if (cells.Count < 8)
                {
                    Logger.Warning($"[OdsParser] Строка {i}: пропущена (ячеек={cells.Count}, требуется 8)");
                    continue;
                }

                var issue = new IdsIssue
                {
                    Requirement = cells[0],
                    Problem = cells[1],
                    IfcClass = cells[2],
                    PredefinedType = cells[3],
                    Name = cells[4],
                    // Description = cells[5],
                    GlobalId = cells[6],
                    Tag = cells[7]
                };

                Logger.Debug($"[OdsParser]   Tag={issue.Tag}, Class={issue.IfcClass}, Property={cells[0].Split(' ')[0]}");

                // Извлекаем PropertyName и DataSet из Requirement
                ParseRequirement(issue);

                // Пропускаем проблемы с несовпадением типов данных
                if (issue.Problem.Contains("does not match the required data type"))
                {
                    Logger.Warning($"[OdsParser] Пропущена проблема несовпадения типов: {issue.PropertyName} для элемента {issue.Tag}");
                    continue;
                }

                if (!string.IsNullOrEmpty(issue.PropertyName))
                {
                    Logger.Debug($"[OdsParser]   ✓ Добавлена проблема: {issue.PropertyName} для {issue.Tag}");
                    issues.Add(issue);
                }
                else
                {
                    Logger.Warning($"[OdsParser]   ✗ PropertyName пустой для строки {i}");
                }
            }

            return issues;
        }

        private List<string> GetRowCells(XElement row)
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements(_tableNs + "table-cell"))
            {
                var repeatCount = cell.Attribute(_tableNs + "number-columns-repeated");
                var count = repeatCount != null ? int.Parse(repeatCount.Value) : 1;

                var text = cell.Descendants(_textNs + "p")
                    .Select(p => p.Value)
                    .FirstOrDefault() ?? "";

                for (int i = 0; i < count; i++)
                {
                    cells.Add(text);
                }
            }
            return cells;
        }

        private void ParseRequirement(IdsIssue issue)
        {
            var req = issue.Requirement;

            // Извлекаем PropertyName (до "data shall") - используем .+? для поддержки кириллицы
            var match = Regex.Match(req, @"^(.+?)\s+data\s+shall");
            if (match.Success)
            {
                issue.PropertyName = match.Groups[1].Value.Trim();
                Logger.Debug($"[OdsParser] PropertyName извлечен: '{issue.PropertyName}'");
            }
            else
            {
                Logger.Warning($"[OdsParser] Не удалось извлечь PropertyName из: '{req}'");
            }

            // Извлекаем DataSet (после "in the dataset")
            var dataSetMatch = Regex.Match(req, @"in the dataset\s+(.+?)(?:\s*$|\s+and\s+)");
            if (dataSetMatch.Success)
            {
                issue.DataSet = dataSetMatch.Groups[1].Value.Trim();
                Logger.Debug($"[OdsParser] DataSet извлечен: '{issue.DataSet}'");
            }

            // Извлекаем enumeration
            var enumMatch = Regex.Match(req, @"'enumeration':\s*\[(.*?)\]");
            if (enumMatch.Success)
            {
                issue.RestrictionType = RestrictionType.Enumeration;
                issue.AllowedValues = Regex.Matches(enumMatch.Groups[1].Value, @"'([^']*)'")
                    .Cast<Match>()
                    .Select(m => m.Groups[1].Value)
                    .ToList();
                return;
            }

            // Извлекаем pattern
            var patternMatch = Regex.Match(req, @"'pattern':\s*'([^']*)'");
            if (patternMatch.Success)
            {
                issue.RestrictionType = RestrictionType.Pattern;
                issue.Pattern = patternMatch.Groups[1].Value;
                return;
            }

            // Извлекаем minLength
            var minLengthMatch = Regex.Match(req, @"'minLength':\s*(\d+)");
            if (minLengthMatch.Success)
            {
                issue.RestrictionType = RestrictionType.MinLength;
                issue.MinLength = int.Parse(minLengthMatch.Groups[1].Value);
                return;
            }

            issue.RestrictionType = RestrictionType.None;
        }

    #endregion

        #region Value Generator

        public class ValueGenerator
        {
            public string GenerateMinValue(IdsIssue issue, string dataType)
            {
                switch (issue.RestrictionType)
                {
                    case RestrictionType.Enumeration:
                        return issue.AllowedValues.Count > 0 ? issue.AllowedValues[0] : GetDefaultForDataType(dataType);
                    case RestrictionType.Pattern:
                        return GenerateFromPattern(issue.Pattern);
                    case RestrictionType.MinLength:
                        return "не требуется";
                    default:
                        return GetDefaultForDataType(dataType);
                }
            }

            private string GetDefaultForDataType(string dataType)
            {
                if (string.IsNullOrEmpty(dataType)) return "не требуется";
                var upper = dataType.ToUpper();

                if (upper.Contains("REAL") || upper.Contains("LENGTH") || upper.Contains("MEASURE") || upper.Contains("ANGLE") || upper.Contains("COUNT") || upper.Contains("RATIO") || upper.Contains("INTEGER"))
                    return "99999";
                if (upper.Contains("BOOLEAN") || upper.Contains("LOGICAL"))
                    return "1";

                return "не требуется";
            }

            private string GenerateFromPattern(string pattern)
            {
                if (string.IsNullOrEmpty(pattern)) return "не требуется";
                try
                {
                    var result = GenerateFromPatternInternal(pattern);
                    return string.IsNullOrEmpty(result) ? "не требуется" : result;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[ValueGenerator] Ошибка генерации для паттерна '{pattern}': {ex.Message}");
                    return "не требуется";
                }
            }

            private string GenerateFromPatternInternal(string pattern)
            {
                var result = "";
                int i = 0;

                while (i < pattern.Length)
                {
                    if (pattern[i] == '^' || pattern[i] == '$') { i++; continue; }

                    if (pattern[i] == '(')
                    {
                        var groupEnd = FindGroupEnd(pattern, i);
                        var groupContent = pattern.Substring(i + 1, groupEnd - i - 1);
                        if (groupContent.StartsWith("?:")) groupContent = groupContent.Substring(2);

                        var alternatives = SplitAlternatives(groupContent);
                        if (alternatives.Count > 0) result += GenerateFromPatternInternal(alternatives[0]);
                        i = groupEnd + 1;
                    }
                    else if (pattern[i] == '[')
                    {
                        var classEnd = pattern.IndexOf(']', i);
                        if (classEnd == -1) break;
                        result += GenerateFromClass(pattern.Substring(i + 1, classEnd - i - 1));
                        i = classEnd + 1;
                    }
                    else if (pattern[i] == '\\')
                    {
                        if (i + 1 < pattern.Length)
                        {
                            if (pattern[i + 1] == 'd') result += "0";
                            else if (pattern[i + 1] == 'w') result += "a";
                            else result += pattern[i + 1];
                            i += 2;
                        }
                        else i++;
                    }
                    else if (pattern[i] == '.')
                    {
                        if (i + 1 < pattern.Length && (pattern[i + 1] == '*' || pattern[i + 1] == '+'))
                        {
                            result += "a";
                            i += 2;
                        }
                        else
                        {
                            result += "a";
                            i++;
                        }
                    }
                    else if (pattern[i] == '*' || pattern[i] == '?' || pattern[i] == '+')
                    {
                        i++;
                    }
                    else if (pattern[i] == '{')
                    {
                        var braceEnd = pattern.IndexOf('}', i);
                        if (braceEnd != -1) i = braceEnd + 1;
                        else i++;
                    }
                    else
                    {
                        result += pattern[i];
                        i++;
                    }
                }
                return result;
            }

            private int FindGroupEnd(string pattern, int start)
            {
                int depth = 0;
                for (int i = start; i < pattern.Length; i++)
                {
                    if (pattern[i] == '(') depth++;
                    else if (pattern[i] == ')')
                    {
                        depth--;
                        if (depth == 0) return i;
                    }
                }
                return pattern.Length - 1;
            }

            private List<string> SplitAlternatives(string content)
            {
                var alternatives = new List<string>();
                int depth = 0, start = 0;
                for (int i = 0; i < content.Length; i++)
                {
                    if (content[i] == '(' || content[i] == '[') depth++;
                    else if (content[i] == ')' || content[i] == ']') depth--;
                    else if (content[i] == '|' && depth == 0)
                    {
                        alternatives.Add(content.Substring(start, i - start));
                        start = i + 1;
                    }
                }
                alternatives.Add(content.Substring(start));
                return alternatives;
            }

            private string GenerateFromClass(string classContent)
            {
                if (classContent.StartsWith("^")) return "a";
                var parts = classContent.Split('-');
                if (parts.Length == 2) return parts[0];
                return classContent.Length > 0 ? classContent[0].ToString() : "a";
            }
        }

        #endregion

        #region Command

        [Transaction(TransactionMode.Manual)]
        public class FixIdsIssuesCommand : IExternalCommand
        {
            public static string IS_TAB_NAME => "ISTools";
            public static string IS_NAME => "Заполнить IDS по отчету";
            public static string IS_IMAGE => "IDS.FillParameters.Resources.ids_fix.png";
            public static string IS_DESCRIPTION => "Автоматическое заполнение параметров по ODS отчету IfcTester";

            public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            {
                ConfigureLogging(commandData);

                try
                {
                    Logger.Info("[FixIdsIssuesCommand] Старт команды");
                    var uidoc = commandData.Application.ActiveUIDocument;
                    var doc = uidoc?.Document;

                    if (doc == null)
                    {
                        Logger.Error("[FixIdsIssuesCommand] Активный документ не найден");
                        message = "Необходимо открыть документ Revit";
                        return Result.Failed;
                    }

                    var odsPath = SelectOdsFile();
                    if (string.IsNullOrEmpty(odsPath)) return Result.Cancelled;

                    var mappingPath = SelectMappingFile();
                    if (string.IsNullOrEmpty(mappingPath)) return Result.Cancelled;

                    var mappingParser = new MappingParser();
                    var mapping = mappingParser.ParseMapping(mappingPath);
                    Logger.Info($"[FixIdsIssuesCommand] Размер маппинга: {mapping.Count}");

                    var odsParser = new OdsParser();
                    var issues = odsParser.Parse(odsPath);

                    if (issues.Count == 0)
                    {
                        TaskDialog.Show("IDS Fix", "Проблем не найдено в ODS отчете");
                        return Result.Succeeded;
                    }

                    // Фильтруем проблемы: оставляем те, где есть валидный ID (Tag) ИЛИ валидный GlobalId
                    var validIssues = issues.Where(i =>
                    {
                        bool hasValidTag = !string.IsNullOrWhiteSpace(i.Tag) &&
                                           i.Tag.Trim().ToLower() != "none" &&
                                           int.TryParse(i.Tag, out _);
                        bool hasValidGuid = !string.IsNullOrWhiteSpace(i.GlobalId);
                        return hasValidTag || hasValidGuid;
                    }).ToList();

                    var groupedIssues = new List<ElementIssues>();

                    // Группируем по уникальному ключу (ID или GUID)
                    var groups = validIssues.GroupBy(i =>
                    {
                        bool hasValidTag = !string.IsNullOrWhiteSpace(i.Tag) &&
                                           i.Tag.Trim().ToLower() != "none" &&
                                           int.TryParse(i.Tag, out _);
                        return hasValidTag ? $"ID_{i.Tag}" : $"GUID_{i.GlobalId}";
                    });

                    foreach (var group in groups)
                    {
                        var firstIssue = group.First();
                        var elementIssue = new ElementIssues { Issues = group.ToList() };

                        // ИСПРАВЛЕНИЕ ОШИБКИ CS0165:
                        // Выносим парсинг ID в отдельный блок, чтобы гарантировать инициализацию переменной 'id'
                        bool hasValidTag = false;
                        int id = 0;

                        if (!string.IsNullOrWhiteSpace(firstIssue.Tag) &&
                            firstIssue.Tag.Trim().ToLower() != "none")
                        {
                            hasValidTag = int.TryParse(firstIssue.Tag, out id);
                        }

                        if (hasValidTag)
                        {
                            elementIssue.Tag = firstIssue.Tag;
                            elementIssue.ElementId = id;
                            elementIssue.IsGuidSearch = false;
                        }
                        else
                        {
                            elementIssue.GlobalId = firstIssue.GlobalId;
                            elementIssue.IsGuidSearch = true;
                        }
                        groupedIssues.Add(elementIssue);
                    }

                    var generator = new ValueGenerator();
                    int totalFixed = 0, totalSkipped = 0, totalNotFound = 0;

                    using (var transaction = new Transaction(doc, "Fix IDS Issues"))
                    {
                        transaction.Start();

                        foreach (var elementIssues in groupedIssues)
                        {
                            Element element = null;

                            if (elementIssues.IsGuidSearch)
                            {
                                element = FindElementByIfcGuid(doc, elementIssues.GlobalId);
                                if (element == null)
                                {
                                    Logger.Warning($"[FixIdsIssuesCommand] Элемент с IfcGUID={elementIssues.GlobalId} не найден");
                                    totalNotFound++;
                                    continue;
                                }
                            }
                            else
                            {
                                element = FindElementById(doc, elementIssues.ElementId);
                                if (element == null)
                                {
                                    Logger.Warning($"[FixIdsIssuesCommand] Элемент с ID={elementIssues.ElementId} не найден");
                                    totalNotFound++;
                                    continue;
                                }
                            }

                            foreach (var issue in elementIssues.Issues)
                            {
                                if (!mapping.TryGetValue(issue.PropertyName, out MappingEntry mappingEntry))
                                {
                                    Logger.Warning($"[FixIdsIssuesCommand] Маппинг не найден для {issue.PropertyName}");
                                    totalSkipped++;
                                    continue;
                                }

                                var param = EnsureParameterOnElement(doc, element, mappingEntry.RevitParameterName, mappingEntry.DataType);
                                if (param == null)
                                {
                                    Logger.Warning($"[FixIdsIssuesCommand] Не удалось создать/найти параметр '{mappingEntry.RevitParameterName}' для элемента {element.Id}");
                                    totalSkipped++;
                                    continue;
                                }

                                if (param.IsReadOnly)
                                {
                                    Logger.Warning($"[FixIdsIssuesCommand] Параметр '{mappingEntry.RevitParameterName}' доступен только для чтения");
                                    totalSkipped++;
                                    continue;
                                }

                                var value = generator.GenerateMinValue(issue, mappingEntry.DataType);

                                try
                                {
                                    SetParameterValue(param, value, mappingEntry.DataType);
                                    Logger.Debug($"[FixIdsIssuesCommand] Заполнен параметр {mappingEntry.RevitParameterName} = '{value}' для элемента {element.Id}");
                                    totalFixed++;
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"[FixIdsIssuesCommand] Ошибка заполнения параметра {mappingEntry.RevitParameterName}: {ex.Message}");
                                    totalSkipped++;
                                }
                            }
                        }
                        transaction.Commit();
                    }

                    var unmappedProperties = issues.Select(i => i.PropertyName).Where(p => !string.IsNullOrEmpty(p) && !mapping.ContainsKey(p)).Distinct().ToList();
                    string unmappedReport = unmappedProperties.Count > 0 ? "\n\n❌ Незамапленные параметры:\n" + string.Join("\n", unmappedProperties.Take(10)) + (unmappedProperties.Count > 10 ? "\n..." : "") : "";

                    TaskDialog.Show("IDS Fix", $"Заполнение завершено:\n✅ Заполнено: {totalFixed}\n⚠️ Пропущено: {totalSkipped}\n❌ Элементов не найдено: {totalNotFound}{unmappedReport}");
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex, "[FixIdsIssuesCommand] Ошибка выполнения команды");
                    message = ex.Message;
                    return Result.Failed;
                }
            }

            private void ConfigureLogging(ExternalCommandData commandData)
            {
                Logger.SetLogPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "i-savelev", "IdsFiller.log"));
                Logger.SetLogLevel(Logger.LogLevel.Debug);
                Logger.Init(hostName: "Autodesk Revit", hostVersionNumber: commandData.Application.Application.VersionNumber, hostBuild: commandData.Application.Application.VersionBuild, hasActiveDocument: commandData.Application.ActiveUIDocument != null);
            }

            private string SelectOdsFile()
            {
                using (var dialog = new System.Windows.Forms.OpenFileDialog())
                {
                    dialog.Title = "Выберете файл ODS";
                    dialog.Filter = "ODS файлы (*.ods)|*.ods|Все файлы (*.*)|*.*";
                    return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
                }
            }

            private string SelectMappingFile()
            {
                using (var dialog = new System.Windows.Forms.OpenFileDialog())
                {
                    dialog.Title = "Выберете файл мэппинга";
                    dialog.Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*";
                    return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
                }
            }

            private void SetParameterValue(Parameter param, string value, string dataType)
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        param.Set(value);
                        break;
                    case StorageType.Integer:
                        if (int.TryParse(value, out int intVal)) param.Set(intVal);
                        else param.Set(1);
                        break;
                    case StorageType.Double:
                        if (double.TryParse(value, out double dblVal)) param.Set(dblVal);
                        else param.Set(99999.0);
                        break;
                    case StorageType.ElementId:
                        Logger.Warning($"[SetParameterValue] Параметр типа ElementId не поддерживается");
                        break;
                    default:
                        Logger.Warning($"[SetParameterValue] Неизвестный тип параметра: {param.StorageType}");
                        break;
                }
            }

            private Element FindElementByIfcGuid(Document doc, string ifcGuid)
            {
                if (string.IsNullOrWhiteSpace(ifcGuid)) return null;

                // 1. Приоритетный поиск в Помещениях и Зонах (OST_Rooms, OST_Areas)
                var targetCategories = new[] { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Areas };

                foreach (var bic in targetCategories)
                {
                    var collector = new FilteredElementCollector(doc)
                        .OfCategory(bic)
                        .WhereElementIsNotElementType();

                    foreach (var elem in collector)
                    {
                        var param = elem.LookupParameter("IfcGUID");
                        if (param != null && param.HasValue && param.AsString() == ifcGuid)
                        {
                            Logger.Debug($"[FindElementByIfcGuid] Найден элемент {elem.Id} в категории {bic} по IfcGUID");
                            return elem;
                        }
                    }
                }

                // 2. Fallback: Поиск по всей модели, если элемент не в Rooms/Areas
                Logger.Debug($"[FindElementByIfcGuid] Поиск в ROOMS/AREAS не дал результатов, запускаю полный поиск для {ifcGuid}");
                var allElements = new FilteredElementCollector(doc).WhereElementIsNotElementType();

                foreach (var elem in allElements)
                {
                    var param = elem.LookupParameter("IfcGUID");
                    if (param != null && param.HasValue && param.AsString() == ifcGuid)
                    {
                        Logger.Debug($"[FindElementByIfcGuid] Найден элемент {elem.Id} в общем поиске по IfcGUID");
                        return elem;
                    }
                }

                return null;
            }

            private Element FindElementById(Document doc, int elementId)
            {
                try
                {
                    var elementIdType = typeof(ElementId);
                    var intValProp = elementIdType.GetProperty("IntegerValue");

                    var constructors = elementIdType.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                    foreach (var ctor in constructors)
                    {
                        var parameters = ctor.GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                        {
                            var id = (ElementId)ctor.Invoke(new object[] { elementId });
                            return doc.GetElement(id);
                        }
                    }

                    foreach (var ctor in constructors)
                    {
                        var parameters = ctor.GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(long))
                        {
                            var id = (ElementId)ctor.Invoke(new object[] { (long)elementId });
                            return doc.GetElement(id);
                        }
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[FindElementById] Ошибка: {ex.Message}");
                    return null;
                }
            }

            private Parameter EnsureParameterOnElement(Document doc, Element element, string paramName, string dataType)
            {
                var param = element.LookupParameter(paramName);
                if (param != null) return param;

                var category = element.Category;
                if (category == null)
                {
                    Logger.Warning($"[EnsureParameterOnElement] У элемента ID={element.Id} нет категории");
                    return null;
                }

                Definition def = FindDefinitionByName(doc, paramName);

                if (def != null)
                {
                    Logger.Debug($"[EnsureParameterOnElement] Параметр '{paramName}' есть в проекте, добавляем категорию '{category.Name}'");
                    var currentBinding = doc.ParameterBindings.get_Item(def);
                    if (currentBinding != null)
                    {
                        var newCatSet = doc.Application.Create.NewCategorySet();
                        CategorySet existingCats = null;
                        bool isTypeBinding = false;

                        if (currentBinding is TypeBinding typeBinding)
                        {
                            existingCats = typeBinding.Categories;
                            isTypeBinding = true;
                        }
                        else if (currentBinding is InstanceBinding instanceBinding)
                        {
                            existingCats = instanceBinding.Categories;
                            isTypeBinding = false;
                        }

                        if (existingCats != null)
                        {
                            foreach (Category c in existingCats) newCatSet.Insert(c);
                        }

                        if (!newCatSet.Contains(category))
                        {
                            newCatSet.Insert(category);
                            Binding newBinding = isTypeBinding
                                ? (Binding)doc.Application.Create.NewTypeBinding(newCatSet)
                                : (Binding)doc.Application.Create.NewInstanceBinding(newCatSet);

                            doc.ParameterBindings.ReInsert(def, newBinding);
                        }
                    }
                }
                else
                {
                    Logger.Debug($"[EnsureParameterOnElement] Параметр '{paramName}' отсутствует в проекте, создаем через SharedParameter");
                    var forgeTypeId = GetForgeTypeIdFromDataType(dataType);

                    BuiltInCategory? builtInCat = FindBuiltInCategoryForCategory(doc, category);

                    if (builtInCat.HasValue)
                    {
                        var categoryList = new List<BuiltInCategory> { builtInCat.Value };
                        string paramGuid = Guid.NewGuid().ToString();
                        string groupName = "IdsFiller_Auto";

                        SharedParameterHelper.SetSharedParam(doc, categoryList, groupName, paramGuid, paramName, forgeTypeId);
                        def = SharedParameterHelper.GetLastDefinition();

                        if (def == null)
                        {
                            Logger.Error($"[EnsureParameterOnElement] Не удалось создать параметр '{paramName}' через SharedParameter");
                            return null;
                        }
                    }
                    else
                    {
                        Logger.Warning($"[EnsureParameterOnElement] Не удалось определить BuiltInCategory для '{category.Name}'");
                        return null;
                    }
                }

                var resultParam = element.LookupParameter(paramName);
                if (resultParam == null)
                {
                    Logger.Warning($"[EnsureParameterOnElement] Параметр '{paramName}' не найден у элемента ID={element.Id} после создания");
                }
                return resultParam;
            }

            private BuiltInCategory? FindBuiltInCategoryForCategory(Document doc, Category category)
            {
                var categoryId = category.Id;
                long categoryIdVal = GetElementIdValue(categoryId);

                Logger.Debug($"[FindBuiltInCategoryForCategory] Поиск категории: {category.Name}, Id={categoryIdVal}");

                // Перебираем все BuiltInCategory и ищем совпадение по Id
                foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
                {
                    try
                    {
                        var cat = Category.GetCategory(doc, bic);
                        if (cat != null)
                        {
                            long catIdVal = GetElementIdValue(cat.Id);

                            if (catIdVal == categoryIdVal)
                            {
                                Logger.Debug($"[FindBuiltInCategoryForCategory] Найдено совпадение по Id: {category.Name} → {bic} (Id={catIdVal})");
                                return bic;
                            }
                        }
                    }
                    catch { }
                }

                Logger.Warning($"[FindBuiltInCategoryForCategory] Не найдена категория: {category.Name}");
                return null;
            }

            private long GetElementIdValue(ElementId elementId)
            {
                var idType = elementId.GetType();

                // Пробуем свойство Value (Revit 2024+)
                var valueProp = idType.GetProperty("Value");
                if (valueProp != null)
                {
                    var value = valueProp.GetValue(elementId);
                    if (value is long longVal) return longVal;
                }

                // Пробуем свойство IntegerValue (Revit 2023 и ниже)
                var intValProp = idType.GetProperty("IntegerValue");
                if (intValProp != null)
                {
                    var value = intValProp.GetValue(elementId);
                    if (value is int intVal) return intVal;
                }

                return 0;
            }

            private Definition FindDefinitionByName(Document doc, string paramName)
            {
                var iterator = doc.ParameterBindings.ForwardIterator();
                while (iterator.MoveNext())
                {
                    if (iterator.Key?.Name == paramName) return iterator.Key;
                }
                return null;
            }

            private ForgeTypeId GetForgeTypeIdFromDataType(string dataType)
            {
                if (string.IsNullOrEmpty(dataType)) return SpecTypeId.String.Text;
                var upper = dataType.ToUpper();

                if (upper.Contains("TEXT") || upper.Contains("STRING") || upper.Contains("LABEL"))
                    return SpecTypeId.String.Text;
                if (upper.Contains("BOOLEAN") || upper.Contains("LOGICAL"))
                    return SpecTypeId.Boolean.YesNo;
                if (upper.Contains("INTEGER"))
                    return SpecTypeId.Int.Integer;
                if (upper.Contains("REAL") || upper.Contains("LENGTH") || upper.Contains("MEASURE") ||
                    upper.Contains("ANGLE") || upper.Contains("COUNT") || upper.Contains("RATIO"))
                    return SpecTypeId.Number;

                return SpecTypeId.String.Text;
            }
        }

        #endregion

        #region Shared Parameter Helper

        public static class SharedParameterHelper
        {
            private static ExternalDefinition _definition;
            private static string _lastParamName;

            public static ExternalDefinition GetLastDefinition()
            {
                Logger.Debug($"[SharedParam] GetLastDefinition: '{_lastParamName}' → def={_definition != null}");
                return _definition;
            }

            public static void SetSharedParam(Document doc, List<BuiltInCategory> categoryList, string GROUP_NAME, string PARAM_GUID, string PARAM_NAME, ForgeTypeId forgeTypeId)
            {
                Logger.Debug($"[SharedParam] === Start: '{PARAM_NAME}' ===");
                string tempFile = null;
                try
                {
                    var app = doc.Application;

                    tempFile = CreateTempSharedParameterFile(GROUP_NAME, PARAM_GUID, PARAM_NAME, forgeTypeId);
                    if (string.IsNullOrEmpty(tempFile) || !File.Exists(tempFile))
                    {
                        Logger.Error("[SharedParam] Не удалось создать временный файл параметров");
                        return;
                    }

                    app.SharedParametersFilename = tempFile;
                    DefinitionFile defFile = app.OpenSharedParameterFile();

                    if (defFile == null)
                    {
                        Logger.Error("[SharedParam] defFile is null");
                        return;
                    }

                    DefinitionGroup group = null;
                    try { group = defFile.Groups.get_Item(GROUP_NAME); }
                    catch (Exception ex) { Logger.Debug($"[SharedParam] get_Item failed: {ex.Message}"); }

                    if (group == null)
                    {
                        var allGroups = new List<string>();
                        foreach (DefinitionGroup g in defFile.Groups) allGroups.Add(g.Name);
                        Logger.Error($"[SharedParam] Group '{GROUP_NAME}' not found. Available: [{string.Join(", ", allGroups)}]");
                        return;
                    }

                    _definition = null;
                    foreach (Definition def in group.Definitions)
                    {
                        if (def.Name == PARAM_NAME)
                        {
                            _definition = def as ExternalDefinition;
                            break;
                        }
                    }

                    if (_definition == null)
                    {
                        Logger.Error($"[SharedParam] Parameter '{PARAM_NAME}' not found");
                        return;
                    }

                    _lastParamName = PARAM_NAME;

                    CategorySet catSet = app.Create.NewCategorySet();
                    foreach (var cat in categoryList)
                    {
                        Category category = Category.GetCategory(doc, cat);
                        if (category != null) catSet.Insert(category);
                    }

                    if (catSet.Size == 0)
                    {
                        Logger.Error("[SharedParam] CategorySet is empty!");
                        return;
                    }

                    var bindingMap = doc.ParameterBindings;
                    bool alreadyBound = false;
                    try { alreadyBound = bindingMap.Contains(_definition); }
                    catch (Exception ex) { Logger.Debug($"[SharedParam] Contains check failed: {ex.Message}"); }

                    if (!alreadyBound)
                    {
                        InstanceBinding binding = app.Create.NewInstanceBinding(catSet);
                        try
                        {
                            bool inserted = bindingMap.Insert(_definition, binding);
                            Logger.Info($"[SharedParam] '{PARAM_NAME}' bound. Insert result: {inserted}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[SharedParam] Insert failed: {ex.Message}");
                        }
                    }
                    else
                    {
                        Logger.Info($"[SharedParam] '{PARAM_NAME}' was already bound.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[SharedParam] Init failed for '{PARAM_NAME}': {ex.Message}");
                }
                finally
                {
                    if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                    {
                        try { File.Delete(tempFile); }
                        catch { }
                    }
                    Logger.Debug($"[SharedParam] === End: '{PARAM_NAME}' ===");
                }
            }

            private static string CreateTempSharedParameterFile(string GROUP_NAME, string PARAM_GUID, string PARAM_NAME, ForgeTypeId forgeTypeId)
            {
                try
                {
                    string tempPath = Path.GetTempPath();
                    string fileName = $"Param_{Guid.NewGuid():N}.txt";
                    string fullPath = Path.Combine(tempPath, fileName);

                    // Маппинг ForgeTypeId в строковое представление для shared parameter file
                    string dataTypeStr = "TEXT";
                    if (forgeTypeId == SpecTypeId.String.Text) dataTypeStr = "TEXT";
                    else if (forgeTypeId == SpecTypeId.Boolean.YesNo) dataTypeStr = "YESNO";
                    else if (forgeTypeId == SpecTypeId.Int.Integer) dataTypeStr = "INTEGER";
                    else if (forgeTypeId == SpecTypeId.Number) dataTypeStr = "NUMBER";
                    else if (forgeTypeId == SpecTypeId.Length) dataTypeStr = "LENGTH";
                    else if (forgeTypeId == SpecTypeId.Area) dataTypeStr = "AREA";
                    else if (forgeTypeId == SpecTypeId.Volume) dataTypeStr = "VOLUME";
                    else if (forgeTypeId == SpecTypeId.Angle) dataTypeStr = "ANGLE";

                    var content = new StringBuilder();
                    content.AppendLine("# This is a Revit shared parameter file.");
                    content.AppendLine("# Do not edit manually.");
                    content.AppendLine("*META\tVERSION\tMINVERSION");
                    content.AppendLine("META\t2\t1");
                    content.AppendLine("*GROUP\tID\tNAME");
                    content.AppendLine($"GROUP\t1\t{GROUP_NAME}");
                    content.AppendLine("*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE");
                    content.AppendLine($"PARAM\t{PARAM_GUID}\t{PARAM_NAME}\t{dataTypeStr}\t\t1\t1\tShared parameter\t1\t0");

                    File.WriteAllText(fullPath, content.ToString(), Encoding.Unicode);
                    return fullPath;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[SharedParam] Failed to create temp file: {ex.Message}");
                    return null;
                }
            }
        }

        #endregion
    }
}
