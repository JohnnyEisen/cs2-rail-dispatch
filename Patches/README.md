# Patches

Harmony 补丁目录（`RailCapacityGuard.Patches`）。

本步骤（P0-C）只建立目录与命名空间，**不放任何补丁**。

约定：
- 每个补丁类一个文件，命名 `<目标类型><用途>Patches.cs`；
- 补丁必须可卸载：提供 `Apply()` / `Remove()`，由 Mod 或对应 Service 显式调用；
- 反射调用只允许出现在 `Services/GameApiProbe.cs`（必要时另加 `VanillaCallAdapter`），补丁内不得散落反射；
- 涉及物理 / 寻路 / 进路锁定的补丁必须有失败兜底（读不到就保守拒绝，不得放行）。

后续阶段计划（P0-B 任务 4）：
- P1 列车长度守卫：拦截列车进站请求
- P2 区间容量守卫：拦截列车发车请求
- P3 咽喉区保护：只读 LaneSignal / LaneReservation，**不写入**
- P4 动态寻路代价：注入点待专项审计（PathfindSetupSystem）
- P7 发车间隔：只读 + 建议，写入由玩家确认后执行
