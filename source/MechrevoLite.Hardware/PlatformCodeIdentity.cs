namespace MechrevoLite.Hardware;

/// <summary>平台码身份：EC/固件里读到的机型码与对应项目号。</summary>
public sealed record PlatformCodeIdentity(string Code, int? ProjectId);
