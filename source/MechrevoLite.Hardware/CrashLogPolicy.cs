using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// 崩溃记录的去重策略：同一签名在窗口期内只写一次完整堆栈，其余只累计次数。
/// <para>目的：单个反复抛出的后台异常不会把 crash.txt 撑到几 MB，也不会淹掉同时期的功能日志。</para>
/// </summary>
internal static class CrashLogPolicy
{
	/// <summary>同一签名的完整堆栈最短重复间隔（秒）。</summary>
	internal const int DedupeSeconds = 60;

	/// <summary>crash.txt 超过该大小就轮转，保留一份历史。</summary>
	internal const long MaxBytes = 2L * 1024 * 1024;

	/// <summary>签名 = 来源 + 异常类型 + 第一处栈帧；同类问题才算重复。</summary>
	internal static string Signature(string source, string exceptionType, string firstFrame)
	{
		return (source ?? "") + "|" + (exceptionType ?? "") + "|" + (firstFrame ?? "");
	}

	/// <summary>距离上次完整记录是否已超过窗口期（未记录过则返回 true）。</summary>
	internal static bool ShouldWriteFull(DateTime nowUtc, DateTime? lastUtc, int dedupeSeconds = DedupeSeconds)
	{
		if (!lastUtc.HasValue)
		{
			return true;
		}
		return (nowUtc - lastUtc.Value).TotalSeconds >= dedupeSeconds;
	}

	/// <summary>取堆栈里第一处带方法名的帧（没有就返回空串）。</summary>
	internal static string FirstFrame(string? stack)
	{
		if (string.IsNullOrWhiteSpace(stack))
		{
			return "";
		}
		foreach (string raw in stack.Split('\n'))
		{
			string line = raw.Trim();
			if (line.Length == 0)
			{
				continue;
			}
			if (line.StartsWith("at ", StringComparison.Ordinal))
			{
				return line.Length > 160 ? line.Substring(0, 160) : line;
			}
		}
		return "";
	}

	/// <summary>该文件是否已经大到需要轮转。</summary>
	internal static bool ShouldRotate(long currentBytes)
	{
		return currentBytes > MaxBytes;
	}
}
