# Test 模块功能

- 顶层模块：`MultiStation.Test`
- 子模块：`MultiStation.Test.Dispatch`、`.Station`、`.Parts`、`.Parts.Flow`、`.Adapters`
- 合并依据：判定与调度服务同一件事——决定工件往哪走、算不算通过；量测算法与步骤强绑定
- 功能编号段：F-TST-01 ~ F-TST-10

## 一、功能清单

| 编号 | 功能 | 说明 | 依赖 | 状态 |
|---|---|---|---|---|
| F-TST-01 | 调度分发 | 按配方把工件分配到工位，维护排队与并行关系 | Platform.Config 配方 | 规划 |
| F-TST-02 | 在制品管理 | 记录每件在制品的工位、阶段、耗时 | Data 结果层 | 规划 |
| F-TST-03 | 完工判定 | 汇总各工位结果，输出单件 Pass / Fail 与失败码 | 工位结果事件 | 规划 |
| F-TST-04 | 工位序列执行 | 执行 Start / Pre / Main / Post / End 五序列 | Device 命令、Config 序列 | 规划 |
| F-TST-05 | 工位状态机 | Idle → WaitingTrigger → Pre → Main → Post → Judging | 无 | 规划 |
| F-TST-06 | 量测算法规程 | 滤波、判据、补偿算法与步骤绑定，随序列加载 | Config 阈值与标定 | 规划 |
| F-TST-07 | 失败跳转与 Post 必达 | Pre 或 Main 失败立即转 Post，跳过剩余步骤 | 无 | 规划 |
| F-TST-08 | 断点续测 | 按步骤 checkpoint 续跑；Post 不允许续测 | Data checkpoint | 规划 |
| F-TST-09 | 外部 Bat 脚本执行 | 序列中以 `Kind=Bat` 调用 .bat/.cmd：退出码 0 判 PASS，非 0 或超时判 NG，stdout/stderr 写入工位日志 | Platform.Core.Execution.BatRunner | 已实现（演示） |
| F-TST-10 | 测试启动人工确认 | 手动开始 / 单步 / 外部触发 / 投料全部入口都需现场人工确认，未确认不得进入测试 | Ui 主界面 | 已实现 |

## 二、关键规则

| 规则 | 内容 |
|---|---|
| 五序列 | Start / End 每进程一次；Pre / Main / Post 每件一次 |
| 失败跳转 | Pre 失败不执行 Main；Main 失败不执行后续步骤；两者直接进 Post |
| Post 必达 | 无论成功、失败、取消、异常，Post 都执行且只执行一次 |
| 单步耗时 | 处理 ≤ 50ms，超时操作下沉作业池 |
| 消息优先级 | 启动/停止/设备命令 = Control；完工事件 = Normal；指标 = Background |
| Bat 步骤 | 仅接受 .bat/.cmd；退出码 0 判 PASS，非 0/启动失败/超时判 NG；后台执行不阻塞界面，完成后回界面线程写结果 |
| 人工确认 | 手动开始、单步、外部触发、手动投料、开始自动投料均先弹「人工确认」，取消则不启动任何测试动作并留痕；`--smoke` 冒烟自检启动测试前同样需要人工确认 |
| 默认不自动测试 | 启动后不自动投料、不自动触发：自动投料默认关闭，模拟外部触发需显式设置 `MULTISTATION_ENABLE_TRIGGER=1` 才启用，避免无人工介入进入测试 |

## 三、验收标准

| 编号 | 场景 | 期望 |
|---|---|---|
| A-01 | 正常单件 | Pre → Main → Post 顺序执行，结果 Pass |
| A-02 | Pre 第 3 步失败 | Main 一步不执行，Post 完整执行，结果 Fail |
| A-03 | Main 第 5 步失败 | 后续步骤跳过，Post 完整执行 |
| A-04 | 操作员中止 | Post 仍执行，结果 Aborted 或 Fail |
| A-05 | 断电重启 | 从 checkpoint 续跑；Post 重新完整执行 |
| A-06 | Bat 步骤退出码 0 | 步骤判 PASS，测量值显示 `exit=0`，脚本输出写入工位日志，序列继续 |
| A-07 | Bat 步骤退出码非 0 / 超时 / 文件缺失 | 步骤判 NG，测量值显示 `exit=n` / `超时` / `启动失败`，本件最终判 FAIL，序列继续执行后续步骤 |
| A-08 | Bat 执行期间点击停止 | 终止脚本进程树，当前步骤标记「已停止」，不再回写结果 |
| A-09 | 点击「开始」后选择「否」 | 不进入测试，状态栏显示「测试未开始：未通过人工确认」 |
| A-10 | 点击「开始」后选择「是」 | 进入测试，状态栏显示「测试已开始（已通过人工确认）」 |
| A-11 | 外部触发 / 投料未经确认 | 不启动测试；触发日志记录「未通过人工确认，已忽略」 |
| A-12 | `--smoke` 冒烟自检启动测试 | 先弹「人工确认 · 冒烟自检」；选择「否」不启动测试，界面自检仍完成并退出 |
| A-13 | 正常启动后静置 13 秒 | 不自动投料、不自动触发：无确认弹窗、投料计数为 0、测试状态保持「待开始」 |

## 四、关联

| 类型 | 引用 |
|---|---|
| 规则 | `Ruler/04_测试模块规则.md` T-01 ~ T-07、V-01 ~ V-16 |
| 文档 | `测试模块设计方案_PCTest测试序列_V1.1.docx` 第 2 章 |
| 下游 | Data（结果与明细）、Error（报警）、Ui（看板） |

## 五、验证记录

自动化用例位于 `tests/MultiStation.Ui.Smoke`（`RunBatChecks`），随套件执行，`SUMMARY failures=0`。

| 编号 | 场景 | 期望 | 实测 |
|---|---|---|---|
| BAT-01 | 脚本 `exit /b 0` 并输出文本 | PASS，stdout 捕获 | `exit=0`，输出含 `BAT-OK` |
| BAT-02 | 脚本 `exit /b 2` | NG，退出码透传 | `exit=2`，`passed=False` |
| BAT-03 | 脚本运行 5 秒、超时配置 800ms | NG，强制终止 | `timedOut=True`，`passed=False` |
| BAT-04 | 脚本文件不存在 | NG，不启动进程 | `started=False`，提示「脚本不存在」 |
| BAT-05 | 非 .bat/.cmd 文件 | NG，拒绝执行 | `started=False`，提示「仅支持 .bat / .cmd」 |
| BAT-06 | 序列示例 `Script\Bat\StartQQ.bat`（相对路径 + check 模式） | PASS，相对路径可解析且不真正启动 QQ | `exit=0`，输出含 `[StartQQ]` |
| U-CF01 | 点击「开始」→ 先选「否」→ 再选「是」 | 取消不进入测试；确认后进入测试 | 两次均符合预期 |
| U-CF02 | `--smoke` 冒烟自检 → 人工确认选「否」 | 未确认不启动测试，自检完成并退出 | `testStartConfirmed=False` |
| U-AU01 | 正常启动后静置 13 秒 | 无自动投料、无自动触发 | 无确认弹窗、投料 0、状态待开始 |

示例脚本：`Script/Bat/StartQQ.bat`，对应 MainSeq 第 30 步 `MAIN_BAT_START_QQ`，用于启动本机 QQ（优先 `D:\QQ\QQ.exe`，找不到时回退 `Tencent\QQNT`、`Tencent\QQ\Bin` 等常见安装路径）；找不到 QQ 或启动失败时退出码非 0，步骤判 NG。`StartQQ.bat check` 只解析路径不启动程序。
