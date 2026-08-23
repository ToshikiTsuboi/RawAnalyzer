namespace RawAnalyzer.App.Rendering;

/// <summary>
/// バイリニアデモザイク結果を1面だけ保持するキャッシュ。
/// </summary>
/// <remarks>
/// カラー現像の描画は「モザイク読み出し → デモザイク → LUT適用」の順で、
/// 重いのは前2段。現像スライダー操作では可視領域もBayer位相も変わらず
/// 最後のLUT段だけが変わるため、前2段の結果を使い回せる。
/// 格納した配列は書き換えないので、描画スレッドが読んでいる最中に
/// 別の描画が差し替えても安全。
/// </remarks>
public sealed class DemosaicCache
{
    private readonly object _gate = new();
    private object? _key;
    private ushort[]? _rgb;

    /// <summary>キーが一致する結果を取り出す。</summary>
    /// <param name="key">照合キー。</param>
    /// <returns>一致する結果。なければnull。</returns>
    public ushort[]? TryGet(object key)
    {
        lock (_gate)
        {
            return Equals(_key, key) ? _rgb : null;
        }
    }

    /// <summary>結果を格納する(以前の内容は捨てる)。</summary>
    /// <param name="key">照合キー。</param>
    /// <param name="rgb">デモザイク結果(以後書き換えないこと)。</param>
    public void Store(object key, ushort[] rgb)
    {
        lock (_gate)
        {
            _key = key;
            _rgb = rgb;
        }
    }

    /// <summary>保持している結果を捨てる。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _key = null;
            _rgb = null;
        }
    }
}
