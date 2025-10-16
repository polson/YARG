using System;

public interface ITrackPlayer
{
    event Action NoteHit;
    event Action NoteMissed;
    event Action VisualsReset;
}

public interface ISustainPlayer
{
    event Action SustainGroupBroken;
    event Action SustainGroupEnded;
}

public interface IWhammyPlayer
{
    // The float represents the whammy value (e.g., 0.0 to 1.0)
    event Action<float> WhammyChangedOnSustain;
}

public interface IStarPowerPlayer
{
    // The bool indicates if Star Power is active
    event Action<bool> StarPowerChanged;
}

public interface IReplayPlayer
{
    event Action<double> ReplayTimeChanged;
}