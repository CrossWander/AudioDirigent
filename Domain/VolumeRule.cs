namespace AudioDirigent;

/// <summary>Уровень, на который ставится устройство с именем, содержащим <see cref="Match"/>.</summary>
internal sealed record VolumeRule(string Match, int Percent);
