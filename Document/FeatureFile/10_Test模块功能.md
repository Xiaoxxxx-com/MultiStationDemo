# Test 模块功能

- 顶层模块：`MultiStation.Test`
- 子模块：`MultiStation.Test.Dispatch`、`.Station`、`.Parts`、`.Parts.Flow`、`.Adapters`
- 合并依据：判定与调度服务同一件事——决定工件往哪走、算不算通过；量测算法与步骤强绑定
- 功能编号段：F-TST-01 ~ F-TST-08

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

## 二、关键规则

| 规则 | 内容 |
|---|---|
| 五序列 | Start / End 每进程一次；Pre / Main / Post 每件一次 |
| 失败跳转 | Pre 失败不执行 Main；Main 失败不执行后续步骤；两者直接进 Post |
| Post 必达 | 无论成功、失败、取消、异常，Post 都执行且只执行一次 |
| 单步耗时 | 处理 ≤ 50ms，超时操作下沉作业池 |
| 消息优先级 | 启动/停止/设备命令 = Control；完工事件 = Normal；指标 = Background |

## 三、验收标准

| 编号 | 场景 | 期望 |
|---|---|---|
| A-01 | 正常单件 | Pre → Main → Post 顺序执行，结果 Pass |
| A-02 | Pre 第 3 步失败 | Main 一步不执行，Post 完整执行，结果 Fail |
| A-03 | Main 第 5 步失败 | 后续步骤跳过，Post 完整执行 |
| A-04 | 操作员中止 | Post 仍执行，结果 Aborted 或 Fail |
| A-05 | 断电重启 | 从 checkpoint 续跑；Post 重新完整执行 |

## 四、关联

| 类型 | 引用 |
|---|---|
| 规则 | `Ruler/04_测试模块规则.md` T-01 ~ T-06、V-01 ~ V-15 |
| 文档 | `测试模块设计方案_PCTest测试序列_V1.1.docx` 第 2 章 |
| 下游 | Data（结果与明细）、Error（报警）、Ui（看板） |
