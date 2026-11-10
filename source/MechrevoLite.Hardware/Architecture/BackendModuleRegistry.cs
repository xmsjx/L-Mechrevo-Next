using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>后端模块注册表：按路由结果取唯一模块。</summary>
internal static class BackendModuleRegistry
{
	private static readonly IBackendModule[] Modules =
	{
		new TongfangGcuBackend(),
		new TongfangEcBackend(),
		new TongfangMifsBackend(),
		new BaolongdaWmiBackend(),
		new NucUmiLocalBackend(),
		new XingyaoPmcBackend()
	};

	internal static IBackendModule? Resolve(BackendKind kind)
	{
		for (int i = 0; i < Modules.Length; i++)
		{
			if (Modules[i].Kind == kind)
			{
				return Modules[i];
			}
		}
		return null;
	}
}
