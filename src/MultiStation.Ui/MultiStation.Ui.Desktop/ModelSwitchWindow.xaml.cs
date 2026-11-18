using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MultiStation.Platform.Core.Production;

namespace MultiStation.Ui.Desktop;

/// <summary>
/// 机种切换与维护：左侧机种列表，右侧编辑该机种的 ECU 烧录参数。
/// 点击“切换使用”后主界面当前机种随之变化。
/// </summary>
public partial class ModelSwitchWindow : Window
{
    private readonly ModelProfileStore _store;
    private readonly List<ModelProfile> _models;
    private bool _loading;

    public ModelSwitchWindow(ModelProfileStore store, ModelProfile? current)
    {
        InitializeComponent();
        _store = store;
        _models = [.. store.Load()];

        var index = current is null ? 0 : _models.FindIndex(m => m.Name == current.Name);
        RefreshList(index < 0 ? null : _models[index].Name);
    }

    /// <summary>点击“切换使用”后返回选中的机种。</summary>
    public ModelProfile? SelectedModel { get; private set; }

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        LoadForm(ModelList.SelectedIndex);
    }

    private void LoadForm(int index)
    {
        if (index < 0 || index >= _models.Count)
        {
            return;
        }

        var model = _models[index];
        _loading = true;
        TxtName.Text = model.Name;
        TxtFirmware.Text = model.FirmwarePath ?? string.Empty;
        TxtDeviceIndex.Text = model.CanDeviceIndex.ToString(CultureInfo.InvariantCulture);
        TxtChannelIndex.Text = model.CanChannelIndex.ToString(CultureInfo.InvariantCulture);
        CmbMode.SelectedIndex = model.IsCanFd ? 1 : 0;
        TxtBaud.Text = model.BaudRate.ToString(CultureInfo.InvariantCulture);
        TxtDataBaud.Text = model.DataBaudRate.ToString(CultureInfo.InvariantCulture);
        TxtRequestId.Text = $"0x{model.RequestId:X3}";
        TxtResponseId.Text = $"0x{model.ResponseId:X3}";
        TxtAddress.Text = $"0x{model.MemoryAddress:X8}";
        TxtSecurityLevel.Text = model.SecurityLevel.ToString(CultureInfo.InvariantCulture);
        TxtFingerprint.Text = model.Fingerprint;
        ChkErase.IsChecked = model.EraseMemory;
        TxtRemark.Text = model.Remark ?? string.Empty;
        TxtError.Text = string.Empty;
        _loading = false;
    }

    private void RefreshList(string? selectName)
    {
        _loading = true;
        var names = _models.Select(m => m.Name).ToList();
        ModelList.ItemsSource = names;
        var index = selectName is null ? 0 : _models.FindIndex(m => m.Name == selectName);
        ModelList.SelectedIndex = index >= 0 && index < names.Count ? index : 0;
        _loading = false;
        LoadForm(ModelList.SelectedIndex);
    }

    private bool SaveCurrent(bool showMessage)
    {
        var index = ModelList.SelectedIndex;
        if (index < 0 || index >= _models.Count)
        {
            return false;
        }

        try
        {
            var model = CollectForm();
            _models[index] = model;
            _store.Save(_models);
            RefreshList(model.Name);
            if (showMessage)
            {
                TxtError.Text = "已保存";
                TxtError.Foreground = System.Windows.Media.Brushes.SeaGreen;
            }

            return true;
        }
        catch (FormatException ex)
        {
            TxtError.Foreground = System.Windows.Media.Brushes.Firebrick;
            TxtError.Text = ex.Message;
            return false;
        }
    }

    private ModelProfile CollectForm()
    {
        var name = TxtName.Text.Trim();
        if (name.Length == 0)
        {
            throw new FormatException("机种名称不能为空");
        }

        var deviceIndex = (int)ParseNumber(TxtDeviceIndex.Text, "CAN 设备序号", 99);
        var channelIndex = (int)ParseNumber(TxtChannelIndex.Text, "通道序号", 99);
        var baud = ParseNumber(TxtBaud.Text, "仲裁波特率", 5_000_000);
        var dataBaud = ParseNumber(TxtDataBaud.Text, "数据波特率", 8_000_000);
        var requestId = ParseNumber(TxtRequestId.Text, "请求 ID", 0x1FFF_FFFF);
        var responseId = ParseNumber(TxtResponseId.Text, "响应 ID", 0x1FFF_FFFF);
        var address = ParseNumber(TxtAddress.Text, "烧录地址", 0xFFFF_FFFF);
        var securityLevel = ParseNumber(TxtSecurityLevel.Text, "安全等级", 0xFF);

        return new ModelProfile
        {
            Name = name,
            FirmwarePath = string.IsNullOrWhiteSpace(TxtFirmware.Text) ? null : TxtFirmware.Text.Trim(),
            CanDeviceIndex = deviceIndex,
            CanChannelIndex = channelIndex,
            CanMode = CmbMode.SelectedIndex == 1 ? "fd" : "classic",
            BaudRate = baud,
            DataBaudRate = dataBaud,
            RequestId = (ushort)requestId,
            ResponseId = (ushort)responseId,
            MemoryAddress = address,
            SecurityLevel = (byte)securityLevel,
            Fingerprint = TxtFingerprint.Text.Trim(),
            EraseMemory = ChkErase.IsChecked == true,
            Remark = string.IsNullOrWhiteSpace(TxtRemark.Text) ? null : TxtRemark.Text.Trim(),
        };
    }

    private static uint ParseNumber(string text, string field, uint max)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            throw new FormatException($"{field}不能为空");
        }

        uint parsed;
        try
        {
            parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new FormatException($"{field}格式不正确（可填十进制或 0x 开头十六进制）");
        }

        if (parsed > max)
        {
            throw new FormatException($"{field}超出允许范围（最大 {max}）");
        }

        return parsed;
    }

    private void OnAddModel(object sender, RoutedEventArgs e)
    {
        var model = new ModelProfile { Name = $"新机种{_models.Count + 1}" };
        _models.Add(model);
        _store.Save(_models);
        RefreshList(model.Name);
        TxtName.Focus();
        TxtName.SelectAll();
    }

    private void OnDeleteModel(object sender, RoutedEventArgs e)
    {
        if (_models.Count <= 1)
        {
            TxtError.Foreground = System.Windows.Media.Brushes.Firebrick;
            TxtError.Text = "至少保留一个机种";
            return;
        }

        var index = ModelList.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        _models.RemoveAt(index);
        _store.Save(_models);
        RefreshList(null);
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveCurrent(showMessage: true);

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent(showMessage: false))
        {
            return;
        }

        SelectedModel = _models[ModelList.SelectedIndex];
        _store.SaveSelected(SelectedModel.Name);
        DialogResult = true;
    }

    private void OnBrowseFirmware(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择烧录文件",
            Filter = "固件文件 (*.bin;*.hex)|*.bin;*.hex|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            TxtFirmware.Text = dialog.FileName;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
}
