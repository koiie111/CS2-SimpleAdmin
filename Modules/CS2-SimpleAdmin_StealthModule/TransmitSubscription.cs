namespace CS2_SimpleAdmin_StealthModule;

/// <summary>Applies each subscription transition once, independently of status blocking.</summary>
internal sealed class TransmitSubscription(Action subscribe, Action unsubscribe)
{
    internal bool Active { get; private set; }

    internal void Sync(bool available, int players, int silentAdmins)
    {
        var needed = available && players > 2 && silentAdmins > 0;
        if (needed == Active) return;

        if (needed)
            subscribe();
        else
            unsubscribe();

        Active = needed;
    }
}
