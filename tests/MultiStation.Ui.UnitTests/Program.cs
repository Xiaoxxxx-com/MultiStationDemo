using MultiStation.Ui.Desktop;

namespace MultiStation.Ui.UnitTests;

/// <summary>单元测试入口：基础信息显示区与测试仪器模块，纯逻辑，不启动界面。</summary>
internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        // 固定时间，避免依赖系统时钟
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);

        // BI-01 初始状态：SN / CarrierID / 开始时间为占位符，工单与配方已就绪
        var initial = BasicUnitInfo.Initial(now, BasicUnitInfo.MapRecipe(recipeB: false), "op01（Operator）");
        Check("BI-01",
            initial.Sn == "-" && initial.CarrierId == "-" && initial.StartedAt == "-"
                && initial.WorkOrder == "MO20260102" && initial.Recipe == "A（串行）"
                && initial.Operator == "op01（Operator）",
            $"sn={initial.Sn} carrier={initial.CarrierId} workOrder={initial.WorkOrder} recipe={initial.Recipe} startedAt={initial.StartedAt}");

        // BI-02 新件 SN / CarrierID 格式与零填充
        var unit = BasicUnitInfo.ForNewUnit(now, BasicUnitInfo.MapRecipe(false), "op01（Operator）");
        Check("BI-02",
            unit.Sn == "SN20260102030405" && unit.CarrierId == "CAR030405",
            $"sn={unit.Sn} carrier={unit.CarrierId}");

        // BI-03 工单号按日期生成
        Check("BI-03", unit.WorkOrder == "MO20260102", $"workOrder={unit.WorkOrder}");

        // BI-04 配方映射：B 并行 / A 串行
        Check("BI-04",
            BasicUnitInfo.MapRecipe(true) == "B（并行）" && BasicUnitInfo.MapRecipe(false) == "A（串行）",
            $"B={BasicUnitInfo.MapRecipe(true)} A={BasicUnitInfo.MapRecipe(false)}");

        // BI-05 开始时间与操作员透传
        Check("BI-05",
            unit.StartedAt == "03:04:05" && unit.Operator == "op01（Operator）",
            $"startedAt={unit.StartedAt} operator={unit.Operator}");

        // BI-06 不同时间生成不同的 SN / CarrierID
        var next = BasicUnitInfo.ForNewUnit(now.AddSeconds(1), BasicUnitInfo.MapRecipe(true), "op01（Operator）");
        Check("BI-06",
            next.Sn != unit.Sn && next.CarrierId != unit.CarrierId && next.Recipe == "B（并行）",
            $"first={unit.Sn}/{unit.CarrierId} second={next.Sn}/{next.CarrierId}");

        // BI-07 初始状态与开始后的工单一致（同一时刻）
        var initialSameMoment = BasicUnitInfo.Initial(now, "A（串行）", "op");
        Check("BI-07", initialSameMoment.WorkOrder == unit.WorkOrder,
            $"initial={initialSameMoment.WorkOrder} unit={unit.WorkOrder}");

        // 测试仪器模块（MultiStation.Instruments）
        InstrumentTests.Run();

        // 机种配置
        ModelTests.Run();

        // 真实仪器连通性检查需显式开启：dotnet run -- --hardware
        if (args.Contains("--hardware", StringComparer.OrdinalIgnoreCase))
        {
            InstrumentTests.RunHardware();
        }

        Console.WriteLine($"SUMMARY failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    internal static void Check(string id, bool ok, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        if (!ok)
        {
            _failures++;
        }
    }
}
