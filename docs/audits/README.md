# 阶段偏离审计报告索引

> **规则**：[`docs/SOP/阶段偏离审查-SOP-2026-11-08.md`](../SOP/阶段偏离审查-SOP-2026-11-08.md)
> **维护**：每个阶段结束由独立 subagent 产出一份报告；这里只登记路径与结论，不替代报告正文。

| 阶段 | 日期 | 结论 | 报告 | 后续 |
|---|---|---|---|---|
| S3 v2 | 2026-11-08 | **FAIL**（未登记 Shape gap：Auth 类型面 + Models 门面） | [`2026-11-08-S3-v2-deviation-audit.md`](2026-11-08-S3-v2-deviation-audit.md) | 修 Shape gap 或用户批准登记为 D；修复后新审计者复核；S4 开工前清偿 |
| S3 v3 | 2026-11-08 | **PASS_WITH_REGISTERED_DEVIATIONS**（N1–N8 均 Minor；N1/N2/N3/N8 修复，N4–N7 登记） | [`2026-11-08-S3-v3-deviation-audit.md`](2026-11-08-S3-v3-deviation-audit.md) | 修复/登记后由新独立审计者复核 |
| S3 v3 复核 | 2026-11-08 | **PASS_WITH_REGISTERED_DEVIATIONS**（N1–N8 全核销；仅 R1 D14 锚点措辞） | [`2026-11-08-S3-v3-deviation-recheck.md`](2026-11-08-S3-v3-deviation-recheck.md) | R1 已订正；D14/S4 承接；待用户验收合并 |
| S4 | 2026-11-08 | **PASS_WITH_REGISTERED_DEVIATIONS**（无 Critical/Shape gap；10 条行为级偏离已补 spec §10） | [`2026-11-08-S4-deviation-audit.md`](2026-11-08-S4-deviation-audit.md) | 登记完成；待用户验收合 main；S5 另立 spec |
| S5 | 2026-11-09 | **PASS_WITH_REGISTERED_DEVIATIONS**（无 Critical/Shape gap；3 条低风险偏离已补 spec §10；D15 拆行有效登记） | [`2026-11-09-S5-deviation-audit.md`](2026-11-09-S5-deviation-audit.md) | 登记完成；待用户验收合 main |
| S1–S5 全量回溯 | 2026-11-09 | **PASS_WITH_REGISTERED_DEVIATIONS**（0 Critical；3 Important = S-1 OAuthCredential extra 形状 / U-1 opencode session 头未接线 / G-1 五项 spec 已登记但台账缺 Dn 行；~20 Minor 主要是 spec 偏离表现势性失效） | [`2026-11-09-S1-S5-cumulative-deviation-audit.md`](2026-11-09-S1-S5-cumulative-deviation-audit.md) | 待用户裁决：修 Important 三条或批准登记为 D |
| S1–S5 修复复核 | 2026-11-09 | **PASS_WITH_REGISTERED_DEVIATIONS**（原 §5 六组必需动作全部核销；F-1~F-4/F-4r 文档订正全部清偿；0 Critical/Important） | [`2026-11-09-S1-S5-cumulative-deviation-recheck.md`](2026-11-09-S1-S5-cumulative-deviation-recheck.md) | 已随 `721ac75` 合入 main |
| S6 | 2026-11-09 | **PASS_WITH_REGISTERED_DEVIATIONS**（0 Critical/Important；6 Minor 已全部处置） | [`2026-11-09-S6-deviation-audit.md`](2026-11-09-S6-deviation-audit.md) | 修复后新审计者复核 |
| S6 修复复核 | 2026-11-09 | **PASS_WITH_REGISTERED_DEVIATIONS**（U-1~U-6 全核销；N-1 已订正；0 Critical/Important） | [`2026-11-09-S6-deviation-recheck.md`](2026-11-09-S6-deviation-recheck.md) | 已随 `987b3c0` 合入 main |
