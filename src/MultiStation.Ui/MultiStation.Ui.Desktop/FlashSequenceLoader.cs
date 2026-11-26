using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using MultiStation.Platform.Core.Production;

namespace MultiStation.Ui.Desktop;

/// <summary>从外部烧录工作簿加载的序列及运行时输入要求。</summary>
public sealed record FlashSequenceDefinition(IReadOnlyList<TestSequenceRow> Rows)
{
    public bool RequiresSupplierSerial => Rows.Any(row =>
        row.Command.Contains("DATA={F18C}", StringComparison.OrdinalIgnoreCase));
}

/// <summary>读取 E3Flash 的 OOXML 烧录配置和 FactoryData，不依赖 Excel。</summary>
public static class FlashSequenceLoader
{
    private static readonly XNamespace _ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace _relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace _pkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>工作簿中的一张工作表及其 ZIP 内路径。</summary>
    private sealed record WorkbookSheet(string Name, string EntryPath);

    public static FlashSequenceDefinition Load(ModelProfile model)
    {
        if (string.IsNullOrWhiteSpace(model.FlashSequencePath))
        {
            throw new FormatException("未配置烧录序列路径");
        }

        if (!File.Exists(model.FlashSequencePath))
        {
            throw new FileNotFoundException($"烧录序列不存在：{model.FlashSequencePath}");
        }

        var factoryData = LoadFactoryData(model.FactoryDataPath);
        var rows = new List<TestSequenceRow>();
        var target = "MAIN";

        using var archive = ZipFile.OpenRead(model.FlashSequencePath);
        var sharedStrings = ReadSharedStrings(archive);

        // 新格式：StartSeq/PreSeq/MainSeq/PostSeq/EndSeq 多工作表（18 列）
        var sheets = ReadWorkbookSheets(archive);
        if (sheets.Any(sheet => sheet.Name.Equals("MainSeq", StringComparison.OrdinalIgnoreCase)))
        {
            var workbookRows = LoadWorkbookRows(archive, sharedStrings, sheets, model, factoryData);
            if (workbookRows.Count == 0)
            {
                throw new FormatException("烧录序列没有可执行步骤");
            }

            return new FlashSequenceDefinition(ApplyInstrumentSteps(model, workbookRows));
        }

        var worksheet = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new FormatException("烧录序列缺少 xl/worksheets/sheet1.xml");
        using var stream = worksheet.Open();
        var document = XDocument.Load(stream);

        foreach (var row in document.Descendants(_ns + "row").Skip(1))
        {
            var cells = ReadCells(row, sharedStrings);
            var number = Value(cells, "A");
            var did = Value(cells, "B").ToUpperInvariant();
            var project = Value(cells, "C");
            var hexValue = Value(cells, "D");
            var service = Value(cells, "F");
            if (string.IsNullOrWhiteSpace(number) && string.IsNullOrWhiteSpace(project)
                && string.IsNullOrWhiteSpace(service))
            {
                continue;
            }

            var code = $"{model.Name}_{number}";

            // 工作簿 C 列以 COM= 开头时作为仪器指令行，便于在脚本中配置上电/回读/CAN/断电
            if (project.StartsWith("COM=", StringComparison.OrdinalIgnoreCase))
            {
                var instrumentNumber = string.IsNullOrWhiteSpace(number)
                    ? (rows.Count + 1).ToString(CultureInfo.InvariantCulture)
                    : number;
                var name = string.IsNullOrWhiteSpace(did) ? "仪器指令" : did;
                rows.Add(new TestSequenceRow(
                    "MainSeq", instrumentNumber, $"{model.Name}_{instrumentNumber}", name, "Action",
                    "", "", "", "", "", InstrumentDevice(project),
                    project, "5000", "0", "Abort", "", "T", "T",
                    string.IsNullOrWhiteSpace(hexValue) ? "工作簿仪器指令" : hexValue));
                continue;
            }

            if (project.StartsWith("AutoProgramFlash", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(model.FlashBatchPath))
                {
                    throw new FormatException($"{project} 未配置 Bat 路径");
                }

                rows.Add(new TestSequenceRow(
                    "MainSeq", number, code, project, "Bat", "", project,
                    "", "", "", "bat", $"FILE={model.FlashBatchPath}", "600000", "0",
                    "Abort", "", "T", "T", "外部 AutoLoader；成功退出码取机种配置"));
                continue;
            }

            if (string.Equals(project, "CCU Data", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(service))
            {
                rows.Add(new TestSequenceRow(
                    "MainSeq", number, code, project, "标题", "Hex", project,
                    "", "", "", "", "", "", "", "", "", "T", "T", "后续 DID 使用 CCU 诊断 ID"));
                target = "CCU";
                continue;
            }

            var pdu = ParseHex(service, $"{project} 的 UDSService");
            if (pdu.Length == 0)
            {
                throw new FormatException($"{project} 的 UDSService 不能为空");
            }

            var channel = string.Equals(target, "CCU", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var command = $"COM=UDS,CHANNEL={channel},PDU={Convert.ToHexString(pdu)}";
            if (pdu[0] == 0x2E)
            {
                if (pdu.Length != 3)
                {
                    throw new FormatException($"{project} 的 2E 请求必须只包含 SID 和 DID");
                }

                var writeDid = $"{pdu[1]:X2}{pdu[2]:X2}";
                var data = writeDid == "F18C"
                    ? "{F18C}"
                    : FactoryHex(factoryData, $"2E{writeDid}", project);
                command += $",DATA={data}";
            }

            var sid = pdu[0];
            var expected = sid == 0x22 && hexValue != "-" && !string.IsNullOrWhiteSpace(hexValue)
                ? Convert.ToHexString(ParseHex(hexValue, $"{project} 的 HEXValue"))
                : string.Empty;
            var timeout = sid == 0x11 ? "5000" : "3000";
            var flowControl = sid == 0x11 ? "PostDelay" : "Abort";
            var flowTarget = sid == 0x11 ? "1000" : string.Empty;
            rows.Add(new TestSequenceRow(
                "MainSeq", number, code, project, sid == 0x22 ? "Test" : "Action", "Hex",
                string.IsNullOrWhiteSpace(did) ? project : did, "", "", expected, "can_zlg_card0",
                command, timeout, "0", flowControl, flowTarget, "T", "T",
                $"目标={target}；原始请求={Convert.ToHexString(pdu)}"));
        }

        if (rows.Count == 0)
        {
            throw new FormatException("烧录序列没有可执行步骤");
        }

        return new FlashSequenceDefinition(ApplyInstrumentSteps(model, rows));
    }

    /// <summary>读取 workbook.xml 与关系文件，得到“工作表名 → ZIP 内路径”。</summary>
    private static List<WorkbookSheet> ReadWorkbookSheets(ZipArchive archive)
    {
        var result = new List<WorkbookSheet>();
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbookEntry is null || relsEntry is null)
        {
            return result;
        }

        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var stream = relsEntry.Open())
        {
            var document = XDocument.Load(stream);
            foreach (var relation in document.Descendants(_pkgRelNs + "Relationship"))
            {
                var id = relation.Attribute("Id")?.Value;
                var target = relation.Attribute("Target")?.Value;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(target))
                {
                    targets[id] = target;
                }
            }
        }

        using (var stream = workbookEntry.Open())
        {
            var document = XDocument.Load(stream);
            foreach (var sheet in document.Descendants(_ns + "sheet"))
            {
                var name = sheet.Attribute("name")?.Value ?? string.Empty;
                var id = sheet.Attribute(_relNs + "id")?.Value ?? string.Empty;
                if (name.Length == 0 || !targets.TryGetValue(id, out var target))
                {
                    continue;
                }

                var path = target.StartsWith('/')
                    ? target.TrimStart('/')
                    : $"xl/{target.Replace('\\', '/')}";
                if (archive.GetEntry(path) is not null)
                {
                    result.Add(new WorkbookSheet(name, path));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 读取 18 列多工作表（StartSeq/PreSeq/MainSeq/PostSeq/EndSeq）。
    /// Q 列为 F 时跳过；标题行保留为分组；命令中的 {token} 按机种配置展开。
    /// </summary>
    private static List<TestSequenceRow> LoadWorkbookRows(
        ZipArchive archive,
        string[] sharedStrings,
        IReadOnlyList<WorkbookSheet> sheets,
        ModelProfile model,
        Dictionary<string, string> factoryData)
    {
        var rows = new List<TestSequenceRow>();
        var stageOrder = new[] { "StartSeq", "PreSeq", "MainSeq", "PostSeq", "EndSeq" };

        foreach (var stage in stageOrder)
        {
            var sheet = sheets.FirstOrDefault(item =>
                item.Name.Equals(stage, StringComparison.OrdinalIgnoreCase));
            if (sheet is null)
            {
                continue;
            }

            var entry = archive.GetEntry(sheet.EntryPath)
                ?? throw new FormatException($"烧录序列缺少工作表文件：{sheet.EntryPath}");
            XDocument document;
            using (var stream = entry.Open())
            {
                document = XDocument.Load(stream);
            }

            foreach (var row in document.Descendants(_ns + "row").Skip(1))
            {
                var cells = ReadCells(row, sharedStrings);
                var number = Value(cells, "A");
                var code = Value(cells, "B");
                var project = Value(cells, "C");
                var kind = Value(cells, "D");
                var category = Value(cells, "E");
                var paramName = Value(cells, "F");
                var min = Value(cells, "G");
                var max = Value(cells, "H");
                var equal = Value(cells, "I");
                var device = Value(cells, "J");
                var command = Value(cells, "K");
                var timeout = Value(cells, "L");
                var retry = Value(cells, "M");
                var flowControl = Value(cells, "N");
                var flowTarget = Value(cells, "O");
                var display = Value(cells, "P");
                var enabled = Value(cells, "Q");
                var remark = Value(cells, "R");

                if (string.IsNullOrWhiteSpace(number) && string.IsNullOrWhiteSpace(code)
                    && string.IsNullOrWhiteSpace(project) && string.IsNullOrWhiteSpace(command)
                    && string.IsNullOrWhiteSpace(paramName))
                {
                    continue;
                }

                if (enabled.Equals("F", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var seq = string.IsNullOrWhiteSpace(number)
                    ? (rows.Count + 1).ToString(CultureInfo.InvariantCulture)
                    : number;
                var expanded = ExpandCommand(command, model, factoryData, project);
                rows.Add(new TestSequenceRow(
                    stage,
                    seq,
                    string.IsNullOrWhiteSpace(code) ? $"{model.Name}_{seq}" : code,
                    project,
                    string.IsNullOrWhiteSpace(kind) ? "Action" : kind,
                    category,
                    paramName,
                    min,
                    max,
                    equal,
                    string.IsNullOrWhiteSpace(device) ? InstrumentDevice(expanded) : device,
                    expanded,
                    string.IsNullOrWhiteSpace(timeout) ? "3000" : timeout,
                    string.IsNullOrWhiteSpace(retry) ? "0" : retry,
                    flowControl,
                    flowTarget,
                    string.IsNullOrWhiteSpace(display) ? "T" : display,
                    string.IsNullOrWhiteSpace(enabled) ? "T" : enabled,
                    remark));
            }
        }

        return rows;
    }

    /// <summary>把 {token} 替换为当前机种配置；{factory:KEY} 读取 FactoryData。</summary>
    private static string ExpandCommand(
        string command,
        ModelProfile model,
        Dictionary<string, string> factoryData,
        string project)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var value = command
            .Replace("{flash_batch}", model.FlashBatchPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{can_device}", model.CanDeviceModel, StringComparison.OrdinalIgnoreCase)
            .Replace("{can_index}", model.CanDeviceIndex.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{can_mode}", model.CanMode, StringComparison.OrdinalIgnoreCase)
            .Replace("{can_baud}", model.BaudRate.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{can_dbit}", model.DataBaudRate.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{can_reqid}", model.RequestId.ToString("X", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{can_respid}", model.ResponseId.ToString("X", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{can_ccu_reqid}", model.CcuRequestId.ToString("X", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{can_ccu_respid}", model.CcuResponseId.ToString("X", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{power_port}", model.PowerSupplyPort ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{power_serial}", model.PowerSerialSettings, StringComparison.OrdinalIgnoreCase)
            .Replace("{power_channel}", model.PowerChannel.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{power_voltage}", model.PowerVoltage.ToString("0.###", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{power_current}", model.PowerCurrentLimit.ToString("0.###", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        const string factoryPrefix = "{factory:";
        int start;
        while ((start = value.IndexOf(factoryPrefix, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = value.IndexOf('}', start);
            if (end < 0)
            {
                break;
            }

            var key = value[(start + factoryPrefix.Length)..end].Trim();
            var hex = FactoryHex(factoryData, key, project);
            value = value[..start] + hex + value[(end + 1)..];
        }

        return value;
    }

    /// <summary>
    /// 工作簿已包含 COM=CAN / COM=IT6322A 指令时完全按脚本顺序执行；
    /// 否则自动补齐上电、电流回读、CAN 初始化与断电步骤。
    /// </summary>
    public static IReadOnlyList<TestSequenceRow> ApplyInstrumentSteps(
        ModelProfile model,
        IReadOnlyList<TestSequenceRow> rows)
    {
        if (rows.Any(row => IsInstrumentCommand(row.Command)))
        {
            return rows;
        }

        var result = new List<TestSequenceRow>(rows.Count + 4);
        if (model.PowerVoltage > 0)
        {
            result.Add(CreatePowerRow(model, "InitSeq", "POWER_ON", "IT6322A 上电", "ON"));
            result.Add(CreateCurrentMeasureRow(model));
        }

        var canInserted = false;
        foreach (var row in rows)
        {
            if (!canInserted && row.Command.StartsWith("COM=UDS", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(CreateCanInitRow(model, 0));
                result.Add(CreateCanInitRow(model, 1));
                canInserted = true;
            }

            result.Add(row);
        }

        if (model.PowerVoltage > 0)
        {
            result.Add(CreatePowerRow(model, "EndSeq", "POWER_OFF", "IT6322A 断电", "OFF"));
        }

        return result;
    }

    private static bool IsInstrumentCommand(string command) =>
        command.StartsWith("COM=CAN", StringComparison.OrdinalIgnoreCase)
        || command.StartsWith("COM=IT6322A", StringComparison.OrdinalIgnoreCase);

    private static string InstrumentDevice(string command) =>
        command.StartsWith("COM=CAN", StringComparison.OrdinalIgnoreCase)
            ? "can_zlg_card0"
            : command.StartsWith("COM=IT6322A", StringComparison.OrdinalIgnoreCase) ? "it6322a" : string.Empty;

    private static TestSequenceRow CreateCanInitRow(ModelProfile model, int channel)
    {
        var requestId = channel == 0 ? model.RequestId : model.CcuRequestId;
        var responseId = channel == 0 ? model.ResponseId : model.CcuResponseId;
        return new TestSequenceRow(
            "InitSeq", (channel + 1).ToString(CultureInfo.InvariantCulture), $"CAN_INIT_CH{channel}",
            $"CAN{channel} 初始化", "Action",
            "", "", "", "", "", "can_zlg_card0",
            string.Create(CultureInfo.InvariantCulture,
                $"COM=CAN,DEVICE={model.CanDeviceModel},INDEX={model.CanDeviceIndex},CHANNEL={channel}," +
                $"MODE={model.CanMode},BAUD={model.BaudRate},DBIT={model.DataBaudRate}," +
                $"REQID={requestId:X},RESPID={responseId:X}"),
            "5000", "0", "Abort", "", "T", "T", $"初始化 CAN{channel} 通道及 UDS ID");
    }

    /// <summary>上电后回读产品电流，可选 MIN/MAX 判据。</summary>
    private static TestSequenceRow CreateCurrentMeasureRow(ModelProfile model) => new(
        "InitSeq", "2", "POWER_MEASURE", "产品电流回读", "Action",
        "", "", "", "", "", "it6322a",
        string.Create(CultureInfo.InvariantCulture,
            $"COM=IT6322A,PORT={model.PowerSupplyPort},SERIAL={model.PowerSerialSettings}," +
            $"CH={model.PowerChannel},MEAS=CURR,DELAY=500"),
        "5000", "0", "Abort", "", "T", "T", "上电后读取 IT6322A 回读电流");

    private static TestSequenceRow CreatePowerRow(
        ModelProfile model,
        string stage,
        string code,
        string project,
        string output) => new(
        stage, "1", code, project, "Action",
        "", "", "", "", "", "it6322a",
        string.Create(CultureInfo.InvariantCulture,
            $"COM=IT6322A,PORT={model.PowerSupplyPort},SERIAL={model.PowerSerialSettings}," +
            $"CH={model.PowerChannel},VOLT={model.PowerVoltage:0.###}," +
            $"CURR={model.PowerCurrentLimit:0.###},OUTP={output}"),
        "5000", "0", "Abort", "", "T", "T",
        output == "ON"
            ? $"按机种电压上电（CH{model.PowerChannel}）"
            : "烧录结束关闭电源输出");

    /// <summary>解析配置中的连续 HEX；空白字符可分隔字节。</summary>
    public static byte[] ParseHex(string value, string fieldName, bool allowEmpty = false)
    {
        var compact = string.Concat((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));
        if (compact.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            compact = compact[2..];
        }

        if (compact.Length == 0)
        {
            if (allowEmpty)
            {
                return [];
            }

            throw new FormatException($"{fieldName}不能为空");
        }

        if (compact.Length % 2 != 0 || compact.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new FormatException($"{fieldName}必须是偶数长度的 HEX 字符串");
        }

        return Convert.FromHexString(compact);
    }

    private static string FactoryHex(Dictionary<string, string> values, string key, string project)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"{project} 缺少 FactoryData 配置 {key}");
        }

        return Convert.ToHexString(ParseHex(value, $"FactoryData {key}"));
    }

    private static Dictionary<string, string> LoadFactoryData(string? path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path))
        {
            return values;
        }

        // 程序复制到其他电脑时，models.json 里的 FlashConfig.ini 往往只在原电脑桌面存在。
        // 文件缺失不阻断序列加载：脚本真正用到 {factory:KEY} 时再报缺少具体键。
        if (!File.Exists(path))
        {
            return values;
        }

        var inFactoryData = false;
        foreach (var source in File.ReadLines(path))
        {
            var line = source.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inFactoryData = string.Equals(line, "[FactoryData]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inFactoryData)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');
            values[key] = value;
        }

        return values;
    }

    private static string[] ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document.Descendants(_ns + "si")
            .Select(item => string.Concat(item.Descendants(_ns + "t").Select(text => text.Value)))
            .ToArray();
    }

    private static Dictionary<string, string> ReadCells(XElement row, string[] sharedStrings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in row.Elements(_ns + "c"))
        {
            var reference = cell.Attribute("r")?.Value ?? string.Empty;
            var column = new string(reference.TakeWhile(char.IsLetter).ToArray());
            if (column.Length == 0)
            {
                continue;
            }

            var type = cell.Attribute("t")?.Value;
            var raw = cell.Element(_ns + "v")?.Value ?? string.Empty;
            var value = type switch
            {
                "s" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index >= 0 && index < sharedStrings.Length
                    ? sharedStrings[index]
                    : throw new FormatException($"单元格 {reference} 的共享字符串索引非法"),
                "inlineStr" => string.Concat(cell.Descendants(_ns + "t").Select(text => text.Value)),
                _ => raw,
            };
            result[column] = value.Trim();
        }

        return result;
    }

    private static string Value(Dictionary<string, string> cells, string column) =>
        cells.TryGetValue(column, out var value) ? value : string.Empty;
}
