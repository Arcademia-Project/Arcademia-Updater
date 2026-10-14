namespace ArcademiaUpdater.Models
{
    public enum UpdaterState
    {
        idle,
        startingLauncher,
        closingLauncher,
        restartingLauncher,
        failed,
        checkingForUpdates,
        updatingLauncher,
        waitingOnInternet,
        repairingLauncher,
        retryingLauncher,
    }
}
