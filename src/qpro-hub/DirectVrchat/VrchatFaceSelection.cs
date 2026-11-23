namespace QproFaceTracking.Hub;

internal sealed record VrchatFaceSelection(bool Gaze, bool Tongue, bool Pupils,
    bool Brows, bool CheekPuff, bool CheekSuck)
{
    internal bool HasAdjustments => Gaze || Tongue || Pupils || Brows || CheekPuff || CheekSuck;
}
