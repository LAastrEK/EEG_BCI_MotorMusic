namespace NeuroPiano
{
    internal enum NeuroPianoUserEventState
    {
        NoteOn = 1,
        NoteOff = 2
    }
    internal enum NeuroPianoUserCommand
    {
        StartSSVEPCalibration = 2001,
        StopSSVEPCalibration = 2002,
        PlayVisibleKey = 2003,
        ReleaseCurrentKey = 2004,
        EpochRejected = 2005,
        StartCovertCalibration = 2006,
        CovertTrialResult = 2007
    }
}
