using System;

namespace MechrevoLite.Update;

internal sealed record UpdateInfo(string CurrentVersion, string? LatestVersion, string Channel, bool ChannelFallback, bool UpdateAvailable, string? ReleaseDate, string? Notes, string? FileName, long? Size, string? Sha256, string? DownloadUrl, string? DownloadPage, string? ServerCurrentVersion = null, string? ExternalGcuName = null, string? ExternalGcuUrl = null, long? ExternalGcuSize = null, string? ExternalGcuSha256 = null, string? SetupFileName = null, string? SetupDownloadUrl = null, long? SetupSize = null, string? SetupSha256 = null)
{
	/// <summary>同一个 Release 里同时有免安装更新包和安装包时，更新页才能让用户选。</summary>
	internal bool HasInstallerChoice => !string.IsNullOrWhiteSpace(DownloadUrl)
		&& !string.IsNullOrWhiteSpace(SetupDownloadUrl)
		&& !string.Equals(DownloadUrl, SetupDownloadUrl, StringComparison.Ordinal);

	internal bool HasVerifiablePackage
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(DownloadUrl))
			{
				return UpdatePolicy.IsValidSha256(Sha256);
			}
			return false;
		}
	}
}
