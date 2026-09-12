using System.Reflection;

namespace AudioDirigent;

/// <summary>Версия работающей сборки — в подвале окна и в строке запуска журнала.</summary>
internal static class Build
{
	public static string Version =>
		Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
}
