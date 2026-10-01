namespace BOCCHI.Modules.Automator;

public enum ActivityState
{
    Idle,
    Pathfinding,
    WaitingToStartCriticalEncounter,
    Participating,
    Done,
}

public static class ActivityStateExtensions
{
    public static string ToLabel(this ActivityState state)
    {
        return state switch
        {
            ActivityState.Idle => Ocelot.I18N.T("ui.idle"),
            ActivityState.Pathfinding => Ocelot.I18N.T("ui.pathfinding"),
            ActivityState.WaitingToStartCriticalEncounter => Ocelot.I18N.T("ui.waiting_to_start_ce"),
            ActivityState.Participating => Ocelot.I18N.T("ui.participating"),
            ActivityState.Done => Ocelot.I18N.T("ui.done"),
            _ => Ocelot.I18N.T("ui.unknown"),
        };
    }
}
