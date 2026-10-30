using Godot;
using MegaCrit.Sts2.Core.Modding;

// 统一入口：一个 DLL 里只能有一个 ModInitializer
[ModInitializer("Initialize")]
public static class SuiteEntry
{
    public static void Initialize()
    {
        try { KnowledgeExportEntry.Initialize(); } catch { }
        try { AgentSeatEntry.Initialize(); } catch { }
        try { ClickSeatEntry.Initialize(); } catch { }
    }
}
