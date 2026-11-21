// 风扇曲线保存/应用结果（后端统一口径）：状态 + 技术明细。
// 界面只按状态翻译文案，⛔ 不再判任何硬件事实、不组装曲线、不直接落盘。
namespace MechrevoLite.Hardware
{
	internal enum FanCurveApplyStatus
	{
		Applied,
		SavedNotPushed,
		SavedInactiveProfile,
		RefusedBoostActive,
		SaveFailed
	}

	internal readonly record struct FanCurveApplyOutcome(FanCurveApplyStatus Status, string Detail = "", bool PreserveDirty = false)
	{
		internal bool Ok
			=> Status == FanCurveApplyStatus.Applied
			|| Status == FanCurveApplyStatus.SavedNotPushed
			|| Status == FanCurveApplyStatus.SavedInactiveProfile;
	}
}
