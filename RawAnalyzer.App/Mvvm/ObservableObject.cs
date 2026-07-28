using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RawAnalyzer.App.Mvvm;

/// <summary>
/// INotifyPropertyChangedの自前実装基底クラス。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// プロパティ変更通知を発行する。
    /// </summary>
    /// <param name="propertyName">変更されたプロパティ名(省略時は呼び出し元)。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// フィールドへ値を設定し、変更があれば通知を発行する。
    /// </summary>
    /// <typeparam name="T">プロパティの型。</typeparam>
    /// <param name="field">バッキングフィールド。</param>
    /// <param name="value">新しい値。</param>
    /// <param name="propertyName">プロパティ名(省略時は呼び出し元)。</param>
    /// <returns>値が変更された場合はtrue。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
