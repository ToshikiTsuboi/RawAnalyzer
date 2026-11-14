using RawAnalyzer.App.Compare;
using RawAnalyzer.App.Mvvm;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.ViewModels;

/// <summary>比較中の表示調整欄が参照する、アクティブペインの設定。</summary>
internal sealed class CompareDisplayEditor : ObservableObject
{
    private DisplaySettings _settings = DisplaySettings.CreateDefault(16);
    private double _blackLevelMax = DisplayLevels.MaxCode(16);

    internal event Action<DisplaySettings>? Edited;

    public double MinGainDb => DisplayLevels.MinGainDb;
    public double MaxGainDb => DisplayLevels.MaxGainDb;
    public string GainNote => $"= ×{_settings.GainLinear:0.###}";
    public double BlackLevelMax => _blackLevelMax;

    public double GainDb
    {
        get => _settings.GainDb;
        set => RequestEdit(_settings with { GainDb = Math.Clamp(value, MinGainDb, MaxGainDb) });
    }

    public double Gamma
    {
        get => _settings.Gamma;
        set => RequestEdit(_settings with { Gamma = value });
    }

    public double Contrast
    {
        get => _settings.Contrast;
        set => RequestEdit(_settings with { Contrast = value });
    }

    public double BlackLevel
    {
        get => _settings.BlackCode;
        set => RequestEdit(_settings with { BlackCode = Math.Clamp(value, 0, BlackLevelMax) });
    }

    public double WhiteLevel
    {
        get => _settings.WhiteCode;
        set => RequestEdit(_settings with { WhiteCode = Math.Clamp(value, 0, BlackLevelMax) });
    }

    internal void Load(DisplaySettings settings, int bitDepth)
    {
        DisplaySettings previous = _settings;
        _settings = settings;
        SetProperty(ref _blackLevelMax, (double)DisplayLevels.MaxCode(bitDepth), nameof(BlackLevelMax));

        if (previous.GainDb != settings.GainDb)
        {
            OnPropertyChanged(nameof(GainDb));
            OnPropertyChanged(nameof(GainNote));
        }
        if (previous.Gamma != settings.Gamma)
        {
            OnPropertyChanged(nameof(Gamma));
        }
        if (previous.Contrast != settings.Contrast)
        {
            OnPropertyChanged(nameof(Contrast));
        }
        if (previous.BlackCode != settings.BlackCode)
        {
            OnPropertyChanged(nameof(BlackLevel));
        }
        if (previous.WhiteCode != settings.WhiteCode)
        {
            OnPropertyChanged(nameof(WhiteLevel));
        }
    }

    private void RequestEdit(DisplaySettings settings)
    {
        if (settings != _settings)
        {
            Edited?.Invoke(settings);
        }
    }
}
