using System.Globalization;

namespace MultiStation.Ui.Desktop;

/// <summary>
/// 基础信息显示区的数据模型：SN、CarrierID、工单、配方、操作员、开始时间。
/// 取值规则与界面解耦，便于单元测试。
/// </summary>
public sealed record BasicUnitInfo(
    string Sn,
    string CarrierId,
    string WorkOrder,
    string Recipe,
    string Operator,
    string StartedAt)
{
    /// <summary>未开始测试时的初始展示：SN / CarrierID / 开始时间均为占位符。</summary>
    public static BasicUnitInfo Initial(DateTime now, string recipe, string operatorName) =>
        new("-", "-", WorkOrderOf(now), recipe, operatorName, "-");

    /// <summary>新件测试开始时生成 SN 与 CarrierID，并记录开始时间。</summary>
    public static BasicUnitInfo ForNewUnit(DateTime now, string recipe, string operatorName) =>
        new(
            $"SN{now:yyyyMMddHHmmss}",
            $"CAR{now:HHmmss}",
            WorkOrderOf(now),
            recipe,
            operatorName,
            now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

    /// <summary>工单号按日期生成：MO + yyyyMMdd。</summary>
    public static string WorkOrderOf(DateTime now) => $"MO{now:yyyyMMdd}";

    /// <summary>配方显示名：B 为并行，其余为 A 串行。</summary>
    public static string MapRecipe(bool recipeB) => recipeB ? "B（并行）" : "A（串行）";
}
