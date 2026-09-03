using AgentIsland.Alarm;

namespace AgentIsland.Tests;

public static class TurnAlarmNavigatorTests
{
    public static void RunAll()
    {
        if (TurnAlarmNavigator.ClaudeDesktopUri(null) != "claude://")
            throw new Exception("hidden Claude Desktop must use the registered app URI");
        if (TurnAlarmNavigator.ClaudeDesktopUri("session_123") != "claude://code/session_123")
            throw new Exception("bridge sessions must keep their conversation URI");
        Console.WriteLine("TurnAlarmNavigatorTests GREEN");
    }
}
