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
            if (project.StartsWith("AutoProgramFlash", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(model.FlashBatchPath))
                {
                    throw new FormatException($"{project} 未配置 Bat 路径");
                }

                rows.Add(new TestSequenceRow(
                    "MainSeq", number, code, project, "Bat", "", project,
                    "", "", "", "bat", $"FILE={model.FlashBatchPath}", "600000", "0",
                    "Abort", "", "T", "T", "外部 AutoLoader 退出码 0 判 PASS"));
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

        return new FlashSequenceDefinition(WithInstrumentCommands(model, rows));
    }

    /// <summary>
    /// 将电源开关与 CAN 通道初始化改为序列中的仪器指令：
    /// 首步上电，首个 UDS 步骤前初始化 CAN（含波特率/模式），末步断电。
    /// </summary>
    private static List<TestSequenceRow> WithInstrumentCommands(
        ModelProfile model,
        List<TestSequenceRow> rows)
    {
        var result = new List<TestSequenceRow>(rows.Count + 3);
        if (model.PowerVoltage > 0)
        {
            result.Add(CreatePowerRow(model, "InitSeq", "POWER_ON", "IT6322A 上电", "ON"));
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

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"FactoryData 配置不存在：{path}");
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
