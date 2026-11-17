namespace MultiStation.Ui.Desktop;

/// <summary>PCTest 测试序列中的一行（字段与 PCTest测试序列.xlsx 一致）。</summary>
public sealed record TestSequenceRow(
    string Stage,
    string Number,
    string Code,
    string Project,
    string Kind,
    string ParamCategory,
    string ParamName,
    string Min,
    string Max,
    string Equal,
    string Device,
    string Command,
    string TimeoutMs,
    string Retry,
    string FlowControl,
    string FlowTarget,
    string DisplayEnabled,
    string Enabled,
    string Remark);

/// <summary>PreSeq / MainSeq / PostSeq 中「测试显示使能」不为 F 的步骤。</summary>
public static class TestSequenceData
{
    public static readonly TestSequenceRow[] Rows =
    {
        new("PreSeq", "1", "PRE_TestTrigger", "进站测试触发", "TestProject", "", "", "", "", "", "", "", "", "", "", "", "", "", ""),
        new("PreSeq", "2", "PRE_VAR_INIT", "PreSeq结果初始化", "Action", "Str", "PRE_RESULT", "", "", "", "", "VAR=PRE_RESULT,VALUE=OK", "1000", "1", "", "", "T", "T", "记录本件进站结果 PRE_RESULT=OK：默认按 OK 处理；任一步失败会改成 NG"),
        new("PreSeq", "3", "PRE_R981_WRITE_01_11", "回复PLC触发值10信号", "Action", "Num", "DataBase", "", "", "", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981,VALUE=11", "1000", "1", "", "", "T", "T", "回写 OK=11：进站初始化完成"),
        new("PreSeq", "4", "PRE_TestTrigger", "进站扫托盘码", "TestProject", "", "", "", "", "", "", "", "", "", "", "", "", "", ""),
        new("PreSeq", "5", "PRE_R981_READ_02_20", "等待PLC触发值20信号", "Test", "Num", "DataBase", "", "", "20", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981", "60000", "1", "While(60s)", "Pass", "T", "T", "等 PLC 触发扫码 CarrierID：读 R981 == 20（While(60s) 内未读到判 ERROR）"),
        new("PreSeq", "6", "PRE_SCAN_CARRIER_ID", "扫码 CarrierID", "Test", "Str", "SN", "", "", "", "KeyenceSRX", "OP=Scan,PROMPT=请扫描 CarrierID,VAR=SN", "120000", "3", "Ternary", "Pass?PRE_R981_WRITE_02_21:PRE_SET_NG_SCAN", "T", "T", "扫码 CarrierID：设备=KeyenceSRX（hardware_map 的 Barcode 条目，KEYENCE SR-X 串口读码，留空则回主窗扫码框），条码写入全局变量 SN（需在「测试序列配置 → 全局变量」登记 SN）；成功走 21，失败置 PRE_RESULT=NG 并回 22，再转等 PLC 回 80 → 回 82"),
        new("PreSeq", "7", "PRE_R981_WRITE_02_21", "回复PLC触发值扫码成功信号", "Action", "Num", "DataBase", "", "", "", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981,VALUE=21", "1000", "1", "PassGoto", "PRE_R981_READ_03_50", "T", "T", "回写 OK=21：扫码 CarrierID 成功"),
        new("PreSeq", "8", "PRE_SET_NG_SCAN", "扫码失败置NG", "Action", "Str", "PRE_RESULT", "", "", "", "", "VAR=PRE_RESULT,VALUE=NG", "1000", "1", "", "", "T", "T", "记录本件进站结果 PRE_RESULT=NG：扫码 CarrierID 失败"),
        new("PreSeq", "9", "PRE_R981_WRITE_NG_22", "回复PLC扫码失败信号22", "Action", "Num", "DataBase", "", "", "", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981,VALUE=22", "1000", "1", "PassGoto", "PRE_R981_READ_05_80", "T", "T", "回写 NG=22：扫码失败 → 转等 PLC 回 80（之后按 PRE_RESULT 回 82）"),
        new("PreSeq", "10", "PRE_TestTrigger", "进站MES校验", "TestProject", "", "", "", "", "", "", "", "", "", "", "", "", "", ""),
        new("PreSeq", "11", "PRE_R981_READ_03_50", "等待PLC触发值50信号", "Test", "Num", "DataBase", "", "", "50", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981", "60000", "1", "While(60s)", "Pass", "T", "T", "等 PLC 触发MES 事件 CarrierCheck：读 R981 == 50（While(60s) 内未读到判 ERROR）"),
        new("PreSeq", "12", "PRE_MES_CARRIER_CHECK", "MES CarrierCheck 校验", "Action", "Num", "MES", "", "", "", "MES", "COM=MES,URL=,Para=", "1000", "1", "Ternary", "Pass?PRE_R981_WRITE_03_51:PRE_SET_NG_MES", "T", "F", "占位：MES CarrierCheck 尚未接入（URL/Para 按 MES 配置补全）；启用后成功走 51、失败置 NG 回 52 再转等 80"),
        new("PreSeq", "13", "PRE_R981_WRITE_03_51", "回复PLC MES校验成功信号51", "Action", "Num", "DataBase", "", "", "", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981,VALUE=51", "1000", "1", "PassGoto", "PRE_R981_READ_04_30", "T", "T", "回写 OK=51：MES CarrierCheck 完成（占位阶段先回 OK）"),
        new("PreSeq", "14", "PRE_SET_NG_MES", "MES校验失败置NG", "Action", "Str", "PRE_RESULT", "", "", "", "", "VAR=PRE_RESULT,VALUE=NG", "1000", "1", "", "", "T", "F", "记录本件进站结果 PRE_RESULT=NG：MES CarrierCheck 失败"),
        new("PreSeq", "15", "PRE_R981_WRITE_NG_52", "回复PLC MES校验失败信号52", "Action", "Num", "DataBase", "", "", "", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981,VALUE=52", "1000", "1", "PassGoto", "PRE_R981_READ_05_80", "T", "F", "回写 NG=52：MES 失败 → 转等 PLC 回 80（之后按 PRE_RESULT 回 82）"),
        new("PreSeq", "16", "PRE_TestTrigger", "进站配置下发", "TestProject", "", "", "", "", "", "", "", "", "", "", "", "", "", ""),
        new("PreSeq", "17", "PRE_R981_READ_04_30", "等待PLC触发值30信号", "Test", "Num", "DataBase", "", "", "30", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981", "60000", "1", "While(60s)", "Pass", "T", "T", "等 PLC 触发PLC 配置信息下发：读 R981 == 30（While(60s) 内未读到判 ERROR）"),
        new("PreSeq", "917", "PRE_R981_WRITE_04_31", "回复PLC配置下发成功信号31", "Action", "Num", "DataBase", "", "", "", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981,VALUE=31", "1000", "1", "PassGoto", "PRE_R981_READ_05_80", "T", "T", "回写 OK=31：PLC 配置信息下发完成（占位阶段先回 OK）"),
        new("PreSeq", "918", "PRE_SET_NG_CFG", "配置下发失败置NG", "Action", "Str", "PRE_RESULT", "", "", "", "", "VAR=PRE_RESULT,VALUE=NG", "1000", "1", "PassGoto", "PRE_R981_READ_05_80", "T", "T", "记录本件进站结果 PRE_RESULT=NG：PLC 配置信息下发失败"),
        new("PreSeq", "919", "PRE_R981_WRITE_NG_32", "回复PLC配置下发失败信号32", "Action", "Num", "DataBase", "", "", "", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981,VALUE=32", "1000", "1", "PassGoto", "PRE_SET_NG_CFG", "T", "T", "PLC 配置下发失败：回 NG=32；随后置 PRE_RESULT=NG，等 PLC 回 80 后由 80 分流回 82"),
        new("PreSeq", "920", "PRE_TestTrigger", "进站流程结束", "TestProject", "", "", "", "", "", "", "", "", "", "", "", "", "", ""),
        new("PreSeq", "921", "PRE_R981_READ_05_80", "等待PLC触发值80信号", "Test", "Num", "DataBase", "", "", "80", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981", "60000", "1", "While(60s)", "Pass", "T", "T", "等 PLC 触发进站流程结束：读 R981 == 80（While(60s) 内未读到判 ERROR）"),
        new("PreSeq", "922", "PRE_ROUTE_05_80", "按进站结果分流回81/82", "Wait", "", "结果分流", "", "", "", "", "wait_ms=0", "1000", "1", "SwitchCase", "@PRE_RESULT:NG:PRE_R981_WRITE_NG_82;*:PRE_R981_WRITE_05_81", "T", "T", "按 PRE_RESULT 分流：NG → 回 82（FailEnd，Pre 判 NG）; OK → 回 81（End 正常结束）"),
        new("PreSeq", "923", "PRE_R981_WRITE_NG_82", "回复PLC进站失败信号82", "Action", "Num", "DataBase", "", "", "", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981,VALUE=82", "1000", "1", "FailEnd", "", "T", "T", "回写 NG=82：本件进站失败；PreSeq 到此结束并判 NG（FailEnd），随后运行 PostSeq 清理上位机状态，本件最终判 FAIL"),
        new("PreSeq", "924", "PRE_R981_WRITE_05_81", "回复PLC进站完成信号81", "Action", "Num", "DataBase", "", "", "", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981,VALUE=81", "1000", "1", "End", "", "T", "T", "回写 OK=81：本件进站完成，结束 Pre 段"),
        new("MainSeq", "1", "", "电源测试", "标题", "", "电源测试", "", "", "", "", "", "", "", "", "", "T", "T", ""),
        new("MainSeq", "2", "MAIN_PSW_SET_V", "电源测试", "Action", "Num", "设置电源电压", "", "", "", "power_gwinstek_psw_visa", "COM=SCPI,VOLTAGE=5", "2000", "1", "", "", "T", "T", "PSW 基本功能：设 5V"),
        new("MainSeq", "3", "MAIN_PSW_SET_I", "电源测试", "Action", "Num", "设置电源电流", "", "", "", "power_gwinstek_psw_visa", "COM=SCPI,CURRENT=1", "2000", "1", "", "", "T", "T", "PSW 基本功能：设 1A"),
        new("MainSeq", "4", "MAIN_PSW_OUT_ON", "电源测试", "Action", "Num", "开启电源输出", "", "", "", "power_gwinstek_psw_visa", "COM=SCPI,OUTPUT=ON", "2000", "1", "PostDelay", "300", "T", "T", "PSW 基本功能：开输出"),
        new("MainSeq", "5", "MAIN_PSW_WAIT", "电源测试", "Wait", "", "稳定延时", "", "", "", "", "wait_ms=500", "2000", "0", "", "", "T", "T", "输出稳定等待 500ms"),
        new("MainSeq", "6", "MAIN_PSW_READ_V", "电源测试", "Test", "Num", "voltage", "4.9", "5.1", "", "power_gwinstek_psw_visa", "COM=SCPI", "3000", "1", "", "", "T", "T", "PSW 基本功能：回读电压 4.9~5.1V"),
        new("MainSeq", "7", "MAIN_PSW_READ_I", "电源测试", "Test", "Num", "current", "0", "1", "", "power_gwinstek_psw_visa", "COM=SCPI", "3000", "1", "", "", "T", "T", "PSW 基本功能：回读电流 0~1A"),
        new("MainSeq", "8", "MAIN_PSW_OVP", "电源测试", "Action", "Num", "设置过压保护", "", "", "", "power_gwinstek_psw_visa", "COM=SCPI,OVP=10", "2000", "1", "", "", "T", "T", "PSW 基本功能：OVP=10V（PSW80 下限 8V）"),
        new("MainSeq", "9", "MAIN_PSW_OUT_OFF", "电源测试", "Action", "Num", "关闭电源输出", "", "", "", "power_gwinstek_psw_visa", "COM=SCPI,OUTPUT=OFF", "2000", "1", "PostDelay", "200", "T", "T", "PSW 基本功能：关输出"),
        new("MainSeq", "10", "", "CAN 通信", "标题", "", "CAN 通信", "", "", "", "", "", "", "", "", "", "T", "T", ""),
        new("MainSeq", "11", "MAIN_CAN_RESET", "CAN 通信", "Action", "Num", "复位CAN通道0", "", "", "", "can_zlg_card0", "CHAN=0,CTRL=RESET", "3000", "1", "", "", "T", "T", "CAN 基本功能：通道复位"),
        new("MainSeq", "12", "MAIN_CAN_LOOPBACK", "CAN 通信", "Action", "Num", "CAN环回自检（CH0发CH2收）", "", "", "", "can_zlg_card0", "CHAN=0,CTRL=SELFTEST", "3000", "1", "", "", "T", "T", "CAN 基本功能：CH0 发 0x123 / CH2 收（现场只接 CH0/CH2，两路硬件短接）"),
        new("MainSeq", "13", "", "CAN DBC 编解码", "标题", "", "CAN DBC 编解码", "", "", "", "", "", "", "", "", "", "T", "T", ""),
        new("MainSeq", "14", "MAIN_CAN_DBC_WRITE_REQ", "CAN DBC 编解码", "Action", "Num", "DBC 写请求帧信号", "", "", "", "can_zlg_card0", "COM=DBC,FUNC=DBCWriteSignal,CHAN=0,FRAME=MRH_Test_Hanlder_Req,SIGNAL=Req_Test_Case_ID,VALUE=1234", "3000", "1", "", "", "T", "T", "DBC 补全：不写 dbc 字段 → 用机台 hardware_map_entry.DBCFilePath 编码 0x738（Req_Test_Case_ID=1234），CH0 发出"),
        new("MainSeq", "15", "MAIN_CAN_DBC_READ_REQ", "CAN DBC 编解码", "Test", "Num", "Req_Test_Case_ID", "", "", "1234", "can_zlg_card0", "COM=DBC,FUNC=DBCReadSignal,CHAN=2,FRAME=MRH_Test_Hanlder_Req,SIGNAL=Req_Test_Case_ID", "3000", "1", "", "", "T", "T", "DBC 补全：CH0 发 → CH2 收（硬件短接），按 DBC 解码 0x738 并与 1234 等值比对"),
        new("MainSeq", "16", "MAIN_CAN_DBC_WRITE_RESP", "CAN DBC 编解码", "Action", "Num", "DBC 写响应帧信号", "", "", "", "can_zlg_card0", "COM=DBC,FUNC=DBCWriteSignal,CHAN=0,FRAME=MRH_Test_Hanlder_Resp,SIGNAL=Resp_Test_Case_ID,VALUE=4321", "3000", "1", "", "", "T", "T", "DBC 补全：编码响应帧 0x739（Resp_Test_Case_ID=4321），CH0 发出"),
        new("MainSeq", "17", "MAIN_CAN_DBC_READ_RESP", "CAN DBC 编解码", "Test", "Num", "Resp_Test_Case_ID", "", "", "4321", "can_zlg_card0", "COM=DBC,FUNC=DBCReadSignal,CHAN=2,FRAME=MRH_Test_Hanlder_Resp,SIGNAL=Resp_Test_Case_ID", "3000", "1", "", "", "T", "T", "DBC 补全：CH2 收 0x739 并按 DBC 解码，与 4321 等值比对（64 字节 FD 帧）"),
        new("MainSeq", "18", "", "CAN XCP 标定", "标题", "", "CAN XCP 标定", "", "", "", "", "", "", "", "", "", "T", "T", ""),
        new("MainSeq", "19", "MAIN_CAN_XCP_CONNECT", "CAN XCP 标定", "Action", "Num", "XCP 连接从站", "", "", "", "can_zlg_card0", "COM=XCP,FUNC=Connect,CHAN=0", "2000", "0", "", "", "T", "F", "XCP（示例 A2L 已就位 PCTest_XCP_Sample.a2l）：CH0/CAN0 短接 CH2/CAN2 只能环回，CONNECT 需从站应答；接上 ECU 后把本行 P 列改 T"),
        new("MainSeq", "20", "MAIN_CAN_XCP_READ_VAR", "CAN XCP 标定", "Test", "Num", "EngSpd", "0", "16000", "", "can_zlg_card0", "COM=XCP,FUNC=ReadSingleMeasurement,CHAN=0,VARIABLE=EngSpd", "2000", "0", "", "", "T", "F", "XCP 读变量：EngSpd（示例 A2L 0x40001000，UWORD，0.25*x-40 rpm，物理范围 0~16343.75；判定 0~16000）"),
        new("MainSeq", "21", "MAIN_CAN_XCP_WRITE_VAR", "CAN XCP 标定", "Action", "Num", "XCP 写标定量", "", "", "", "can_zlg_card0", "COM=XCP,FUNC=SetSingleCalibration,CHAN=0,VARIABLE=CalGain,DATA=42", "2000", "0", "", "", "T", "F", "XCP 写变量：CalGain（示例 A2L 0x40002000，UBYTE，恒等换算）写 42，供下一行回读校验"),
        new("MainSeq", "22", "MAIN_CAN_XCP_READ_CAL", "CAN XCP 标定", "Test", "Num", "CalGain", "", "", "42", "can_zlg_card0", "COM=XCP,FUNC=ReadSingleMeasurement,CHAN=0,VARIABLE=CalGain", "2000", "0", "", "", "T", "F", "XCP 写后回读：CalGain 应等于 42（验证换算反算 + 写入 + 读回闭环）"),
        new("MainSeq", "23", "MAIN_CAN_XCP_DAQ_START", "CAN XCP 标定", "Action", "Num", "XCP DAQ 启动", "", "", "", "can_zlg_card0", "COM=XCP,FUNC=ReadMulMeasurement,CHAN=0,VARIABLES=EngSpd,EVENT_CHANNEL=0", "2000", "0", "", "", "T", "F", "XCP DAQ：单 DAQ 列表采 EngSpd（示例 A2L 变量）；多变量需 program JSON（17 列逗号是参数分隔符）"),
        new("MainSeq", "24", "MAIN_CAN_XCP_DAQ_READ", "CAN XCP 标定", "Test", "Num", "EngSpd", "0", "16000", "", "can_zlg_card0", "COM=XCP,FUNC=ReadMulMeasurement,CHAN=0,VARIABLE=EngSpd", "2000", "0", "", "", "T", "F", "XCP DAQ：收一帧 DTO 按变量顺序解码 EngSpd 并判定 0~16000"),
        new("MainSeq", "25", "MAIN_CAN_XCP_DAQ_STOP", "CAN XCP 标定", "Action", "Num", "XCP DAQ 停止", "", "", "", "can_zlg_card0", "COM=XCP,FUNC=Close,CHAN=0", "2000", "0", "", "", "T", "F", "XCP DAQ：停列表并释放 DAQ 配置（收尾，幂等）"),
        new("MainSeq", "26", "", "PLC 交互", "标题", "", "PLC 交互", "", "", "", "", "", "", "", "", "", "T", "T", ""),
        new("MainSeq", "27", "MAIN_PLC_ESTOP_INPUT", "PLC 交互", "Test", "Num", "DataBase", "", "", "0", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=D0.5", "2000", "1", "", "", "T", "T", "PLC DI 急停输入（BOOL，D0.5，期望 0=未按下）"),
        new("MainSeq", "28", "MAIN_PLC_ALARM", "PLC 交互", "Test", "Num", "DataBase", "", "", "0", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R101.8", "2000", "1", "", "", "T", "T", "PLC 系统报警中（BOOL，R101.8，期望 0=无报警）"),
        new("MainSeq", "29", "", "外部脚本", "标题", "", "外部脚本", "", "", "", "", "", "", "", "", "", "T", "T", ""),
        new("MainSeq", "30", "MAIN_BAT_START_QQ", "外部脚本", "Bat", "Num", "启动 QQ", "", "", "", "bat", "FILE=Script\\Bat\\StartQQ.bat", "10000", "1", "", "", "T", "T", "调用 Script\\Bat\\StartQQ.bat 启动 QQ（优先 D:\\QQ\\QQ.exe，回退常见安装路径）：退出码 0 判 PASS，非 0 或超时判 NG；stdout 前 8 行写入工位日志"),
        new("PostSeq", "1", "", "电源测试", "标题", "", "电源测试", "", "", "", "", "", "", "", "", "", "T", "T", ""),
        new("PostSeq", "2", "POST_PSW_OUT_OFF", "电源测试", "Action", "Num", "关闭电源输出", "", "", "", "power_gwinstek_psw_visa", "COM=SCPI,OUTPUT=OFF", "2000", "1", "PostDelay", "200", "T", "T", "每件后段：先关输出（安全底线）"),
        new("PostSeq", "3", "POST_PSW_INIT", "电源测试", "Action", "Num", "初始化电源", "", "", "", "power_gwinstek_psw_visa", "COM=SCPI,CTRL=INIT", "5000", "1", "", "", "T", "T", "PSW 复位 *RST/*CLS 并关闭输出，回到可复用初始态"),
        new("PostSeq", "4", "", "CAN 通信", "标题", "", "CAN 通信", "", "", "", "", "", "", "", "", "", "T", "T", ""),
        new("PostSeq", "5", "POST_CAN_RESET", "CAN 通信", "Action", "Num", "初始化CAN通道", "", "", "", "can_zlg_card0", "CHAN=0,CTRL=RESET", "3000", "1", "", "", "T", "T", "CAN 通道复位回初始态（设备保持打开，EndSeq 才彻底断开）"),
    };
}

/// <summary>测试触发序列中的一行（PCTest测试序列.xlsx 的 TriggerSeq 页签）。</summary>
public sealed record TriggerSequenceRow(
    string Number,
    string Code,
    string Project,
    string Kind,
    string Equal,
    string Device,
    string Command,
    string TimeoutMs,
    string Target,
    string Remark);

/// <summary>外部触发序列：独立线程实时监听触发信号。</summary>
public static class TriggerSequenceData
{
    public static readonly TriggerSequenceRow[] Rows =
    {
        new("1", "TRIG_R981_START", "PLC 交互", "Test", "10", "plc_inovance_modbus", "COM=MODBUS_TCP,POINT=R981", "2000", "开始测试", "PLC 触发：R981（进站握手字）= 10 → 开始测试（唯一触发条件，F-0247）"),
    };
}
