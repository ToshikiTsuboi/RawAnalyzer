using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Globalization;
using System.Text;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    // ---- 保存 ----

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ActiveImage is null || _currentFormat is null)
        {
            return;
        }

        if (RejectWhileImageReplacing("保存"))
        {
            return;
        }

        // ダイアログ表示中も再生タイマーは動くため、ここから保存完了まで差し替えを止める
        using BusyScope busy = EnterBusy();

        // 保存ダイアログはこの画像(とHDR合成の結果)に合わせて作る
        RawImage target = ActiveImage;
        HdrImage? targetFloat = _hdrFloatImage;

        // WIC 経路は表示中の1フレームのみ扱うため、判定も1フレームの画素数で行う。
        // 行交互HDRの Raw 表示で Bayer の位相が一致しない raw は、カラー現像の表示と同じく色を取り違えるので
        // デモザイク(WB・カラーマトリクス)を選ばせない(分割ビュー・合成ビューからは現像して保存できる)
        var dialog = new SaveDialog(
            (long)target.Width * target.Height,
            allowFloatRaw: targetFloat is not null,
            hasBayer: ActiveFormat?.Bayer is not (null or BayerPattern.None) && !BayerPhaseMismatchShown,
            bitDepth: target.Format.BitDepth)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        SaveChoice choice = dialog.Result;
        (string filter, string extension) = choice.Format switch
        {
            SaveFormat.Tiff16 => ("TIFF (*.tif)|*.tif", ".tif"),
            SaveFormat.Png16 or SaveFormat.Png8 => ("PNG (*.png)|*.png", ".png"),
            SaveFormat.Jpeg8 => ("JPEG (*.jpg)|*.jpg", ".jpg"),
            SaveFormat.FloatRaw => ("float raw (*.fraw)|*.fraw", ".fraw"),
            _ => ("Raw (*.raw)|*.raw", ".raw"),
        };
        var fileDialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = Path.GetFileNameWithoutExtension(_currentPath ?? "image")
                + (_tiffStack?.PageSuffix(_tiffPageIndex) ?? "") + extension,
        };
        if (fileDialog.ShowDialog(this) != true)
        {
            return;
        }

        // ダイアログ(入れ子ポンプ)の間に表示中の画像が替わっていたら、ダイアログを作った画像とは別の画像を
        // 保存することになる(替える処理の途中は上で断っているが、保存する直前にも確かめる)
        if (!ReferenceEquals(target, ActiveImage) || !ReferenceEquals(targetFloat, _hdrFloatImage))
        {
            MessageBox.Show(this, "保存の準備中に表示中の画像が替わったため、保存を中止しました。" +
                "表示中の画像を確かめてから、もう一度保存してください。", "RawAnalyzer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 付随テキストの書き出しまで待つ(その間も表示画像を差し替えさせない)
        await ExecuteSaveAsync(choice, fileDialog.FileName);
    }

    private async Task ExecuteSaveAsync(SaveChoice choice, string path)
    {
        if (_tiffStack?.IsSourcePath(path) == true)
        {
            MessageBox.Show(this, "これは現在の1ページだけの保存です。スタック全体を失わないよう、" +
                "元TIFFとは別の名前で保存してください。", "TIFFスタックの保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RawImage image = ActiveImage!;
        HdrImage? hdrFloat = _hdrFloatImage;

        // 読み込んだRGB画像(JPEG/PNG/カラーTIFF)は輝度化せずそのまま保存する。
        // 派生ビュー(HDR分割・合成)の表示中は元のカラーとは別物なので対象外
        ColorImage? colorImage = _derivedImage is null ? _colorImage : null;

        // 表示中のフレームを保存する(rawは全フレーム出力なので対象外)
        int frame = Math.Clamp(Viewport.Frame, 0, image.FrameCount - 1);

        // チェック状態に応じて、適用しない処理は恒等パラメータへ落とす。表示調整の恒等は表示中の画像の
        // ビット深度の既定(白=最大コード<<(16−N))で、8bit は各コードをそのまま書く
        int saveBitDepth = ActiveFormat?.BitDepth ?? 16;
        DisplayLut lut = choice.ApplyDisplayLut
            ? BuildLut()
            : DisplayLut.Create(DisplayLevels.IdentityParameters(saveBitDepth));
        DevelopParameters developParameters = CurrentDevelopParameters();
        if (!choice.ApplyDisplayLut)
        {
            developParameters = developParameters with
            {
                BlackLevel = 0,
                Gamma = 1.0,
                WhitePoint = DisplayLevels.DefaultPoints(saveBitDepth).WhitePoint,
                Gain = 1.0,
                Contrast = 1.0,
            };
        }

        if (!choice.ApplyWhiteBalance)
        {
            developParameters = developParameters with { GainR = 1.0, GainG = 1.0, GainB = 1.0 };
        }

        if (!choice.ApplyMatrix)
        {
            developParameters = developParameters with { Matrix = null };
        }

        ViewportDisplayMode mode = choice.ApplyDemosaic
            ? ViewportDisplayMode.ColorDevelop
            : ViewportDisplayMode.Raw;

        // 保存した raw の記憶に使う Bayer も保存を始めるときに決める(付随テキストの書き出しを待つ間に右パネルで
        // 変えられても、保存した画像・付随テキストと同じパターンを記憶する)
        BayerPattern savedBayer = ActiveFormat?.Bayer ?? image.Format.Bayer;
        BayerPattern pattern = choice.ApplyDemosaic ? savedBayer : BayerPattern.None;
        var devLuts = DevelopLuts.Create(developParameters);

        // HDR分割ビューは表示調整を段ごとに持ち、段ごとのLUTで描く。表示LUTを焼き込むなら画面と同じく
        // 各段をその段の表示調整で焼き込む(スライダーの値は最後に調整した段のもの)。焼き込まないときも各段は
        // 別の画像として焼き込む。段ごとの値はここで控え、付随テキストにも同じ値を書く
        HdrSplitAdjustments? split = HdrSplitAdjustments.ForSave(
            _hdrFrameParams, _hdrSegmentWidth, choice.ApplyDisplayLut, saveBitDepth);

        ProgressWindow result = ProgressWindow.Run(
            this,
            $"保存中: {Path.GetFileName(path)}",
            (progress, ct) => Task.Run(() =>
            {
                switch (choice.Format)
                {
                    case SaveFormat.FloatRaw:
                        hdrFloat!.SaveFloatRaw(path, progress, ct);
                        break;
                    case SaveFormat.Raw:
                        RawSaver.Save(image, path, choice.Packing, choice.Endianness, progress, ct);
                        break;
                    default:
                        // TIFF/PNG/JPEG。1億画素を超えるTIFFは自前ライタで行単位に書く
                        ImageFileSaver.Save(image, frame, path, choice.Format, mode, pattern,
                            lut, devLuts, colorImage, progress, ct, split: split);
                        break;
                }
            }, ct));

        if (result.Error is not null)
        {
            MessageBox.Show(this, $"保存に失敗しました: {result.Error.Message}", "RawAnalyzer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else if (!result.WasCanceled)
        {
            string sidecarNote = choice.WriteSidecar
                ? await WriteProcessingSidecarAsync(path, choice, developParameters, split, frame, image.FrameCount)
                : "";

            // RawSaver はヘッダを出力しないため、保存したrawを開き直したときに
            // 元のHeaderOffsetのままだと開けない。出力実体に合うフォーマットを記憶する。
            // Bayerはパネルで変更した値が_currentFormat側にしか反映されないため、
            // 読み込み時のimage.FormatではなくActiveFormatから(保存を始めるときに)取る。以前は付随テキストの
            // 書き出しを待った後で取り、待つ間に右パネルで変えたパターンを、付随テキストと食い違ったまま記憶した
            if (choice.Format == SaveFormat.Raw)
            {
                RememberFileFormat(path, image.Format with
                {
                    HeaderOffset = 0,
                    Packing = choice.Packing,
                    Endianness = choice.Endianness,
                    Bayer = savedBayer,
                });
            }

            // マルチフレームでは「どのフレームを出したか」を明示する
            string frameNote = _tiffStack is not null ? TiffPageNote : image.FrameCount > 1
                ? choice.Format == SaveFormat.Raw
                    ? $" (全{image.FrameCount}フレーム)"
                    : $" (フレーム {frame + 1}/{image.FrameCount})"
                : "";
            _vm.ImageInfoText = $"保存完了: {Path.GetFileName(path)}"
                + frameNote
                + (choice.IsProcessed ? " (処理を焼き込み)" : " (無処理)")
                + sidecarNote;
        }
    }

    /// <summary>保存画像に何が適用されたかを記録するテキストを書き出す。</summary>
    /// <remarks>
    /// 本文は表示状態から UI スレッドで作り、書き出し先の決定(同名の .txt の確認)と書き込みは UI スレッドの外で行う
    /// (<see cref="ProcessingSidecar.Write"/>。保存先はネットワーク上のことがあり、切断していればタイムアウトまで戻らない)。
    /// </remarks>
    /// <param name="imagePath">保存した画像のパス。</param>
    /// <param name="choice">保存ダイアログの選択。</param>
    /// <param name="developParameters">保存に使った現像パラメータ。</param>
    /// <param name="split">HDR分割ビューから保存したときの段ごとの表示調整(分割ビューでなければ null)。</param>
    /// <param name="frame">保存したフレーム番号(raw 形式以外は表示中の1フレームだけを書き出す)。</param>
    /// <param name="frameCount">保存した画像のフレーム数。</param>
    /// <returns>
    /// 保存完了の表示に添える注記。画像と同名の .txt へ書いたときは空。既存のファイルを残すため別名へ書いたとき・
    /// 書けなかったときはそのことを示す。
    /// </returns>
    private async Task<string> WriteProcessingSidecarAsync(
        string imagePath, SaveChoice choice, DevelopParameters developParameters, HdrSplitAdjustments? split,
        int frame, int frameCount)
    {
        try
        {
            // HDR派生ビュー(分割・合成)の表示中も、入力フォーマットは元ファイルの形式(HDR方式を含む)を書く。
            // 派生画像の形式と作り方は[HDR派生ビュー]に書く(派生ビューでなければ ActiveFormat と同じ)
            RawFormat? format = _currentFormat;
            var sb = new StringBuilder();
            sb.AppendLine(ProcessingSidecar.Title);
            sb.AppendLine("====================");
            sb.Append("保存日時: ").AppendLine(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append(ProcessingSidecar.OutputFileLabel).AppendLine(Path.GetFileName(imagePath));
            sb.Append("元ファイル: ").AppendLine(_currentPath ?? "(不明)");
            if (_tiffStack is not null)
            {
                sb.Append("元TIFFのページ: ").AppendLine($"{_tiffPageIndex + 1}/{_tiffStack.PageCount}");
            }

            // マルチフレームの画像から1フレームだけを書き出したら、どのフレームかを書く
            if (ProcessingSidecar.SourceFrameLine(frameCount, frame, _tiffStack is not null, choice.Format)
                is { } frameLine)
            {
                sb.AppendLine(frameLine);
            }
            if (_correctionLabel is not null)
            {
                sb.Append("適用済み補正: ").AppendLine(_correctionLabel);
            }

            sb.AppendLine();
            sb.AppendLine("[入力フォーマット]");
            if (format is not null)
            {
                sb.Append("  サイズ: ").Append(format.Width).Append('×')
                    .AppendLine(format.Height.ToString(CultureInfo.InvariantCulture));
                sb.Append("  ビット深度: ").Append(format.BitDepth)
                    .Append("bit ").AppendLine(
                        format.Packing == BitPacking.Lsb ? "下詰め" : "上詰め");
                sb.Append("  エンディアン: ").AppendLine(format.Endianness.ToString());
                sb.Append("  Bayer: ").AppendLine(format.Bayer.ToString());
                sb.Append("  HDR: ").AppendLine(HdrFormatText.DescribeSidecar(format));
            }

            // HDR派生ビューから保存した画像は元ファイルの画素ではない。どの派生ビューか(合成なら合成で減算した黒点・
            // 露光比・量子化)を書く。[適用処理]の黒点/白点は合成ビューの表示黒点・白点(合成画像の値域で黒0・白65535
            // から始まる)で、合成で減算した黒点とは別物になる
            // 分割ビューは段の構成(段の数・幅・各段の露光・各段の Bayer の数え方)も書く(保存の形式によらない)
            if (format is not null
                && HdrViewSidecar.DescribeDerivedView(
                    _derivedImage?.Format, _derivedImage?.SegmentWidth ?? 0, _hdrFloatImage,
                    _hdrFrameParams?.Length ?? format.HdrStages, format, _hdrSourceFrame.Frame, choice.Format)
                    is { } derivedView)
            {
                sb.AppendLine();
                sb.Append(derivedView);
            }

            sb.AppendLine();
            sb.AppendLine("[出力]");
            sb.Append("  形式: ").AppendLine(choice.Format.ToString());
            if (choice.Format == SaveFormat.Raw && choice.RawBytesPerPixel == 1)
            {
                // 8bit 以下の画像は1画素1バイトで書き、詰め方向・エンディアンはない
                sb.AppendLine("  画素: 1バイト/画素 (8bit)");
            }
            else if (choice.Format == SaveFormat.Raw)
            {
                sb.Append("  詰め方向: ").AppendLine(
                    choice.Packing == BitPacking.Lsb ? "下詰め (LSB)" : "上詰め (MSB)");
                sb.Append("  エンディアン: ").AppendLine(choice.Endianness.ToString());
            }

            sb.Append("  要約: ").AppendLine(choice.Summary);

            sb.AppendLine();
            sb.AppendLine("[適用処理]");

            // HDR分割ビューから表示LUTを焼き込んだ画像は各段をその段の表示調整で焼き込んでいる。スライダーの値は
            // 最後に調整した段のもので、1組だけ書くと実体と食い違うので段ごとに書く
            HdrSplitAdjustments? stageAdjustments = choice.ApplyDisplayLut ? split : null;
            if (stageAdjustments is not null)
            {
                sb.Append(HdrViewSidecar.DescribeSplitDisplayLut(stageAdjustments.Stages));
            }
            else
            {
                sb.Append("  表示LUT: ").AppendLine(choice.ApplyDisplayLut ? "適用" : "なし");
            }

            if (choice.ApplyDisplayLut && stageAdjustments is null)
            {
                sb.Append("    黒点/白点: ").Append(_blackPoint).Append(" / ")
                    .AppendLine(_whitePoint.ToString(CultureInfo.InvariantCulture));
                sb.Append("    ゲイン: ")
                    .Append(_vm.GainDb.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(" dB (×")
                    .Append(_vm.Gain.ToString("F3", CultureInfo.InvariantCulture))
                    .AppendLine(")");
                sb.Append("    ガンマ: ").AppendLine(
                    _vm.Gamma.ToString("F3", CultureInfo.InvariantCulture));
                sb.Append("    コントラスト: ").AppendLine(
                    _vm.Contrast.ToString("F3", CultureInfo.InvariantCulture));
            }

            // WB/マトリクスはデモザイク経路でのみ画素に適用される。
            // チェックだけ見て「適用」と書くと、デモザイクOFF保存で来歴が実体と食い違う
            bool wbApplied = choice.ApplyWhiteBalance && choice.ApplyDemosaic;
            bool matrixApplied =
                choice.ApplyMatrix && choice.ApplyDemosaic && !_colorMatrix.IsIdentity;
            sb.Append("  ホワイトバランス: ").AppendLine(
                wbApplied ? "適用"
                : choice.ApplyWhiteBalance ? "なし(デモザイクなしのため未適用)" : "なし");
            if (wbApplied)
            {
                sb.Append("    R/G/B ゲイン: ")
                    .Append(developParameters.GainR.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" / ")
                    .Append(developParameters.GainG.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" / ")
                    .AppendLine(developParameters.GainB.ToString("F3", CultureInfo.InvariantCulture));
            }

            sb.Append("  カラーマトリクス: ").AppendLine(
                matrixApplied ? "適用"
                : choice.ApplyMatrix && !_colorMatrix.IsIdentity
                    ? "なし(デモザイクなしのため未適用)" : "なし");
            if (matrixApplied)
            {
                double[] m = _colorMatrix.ToArray();
                for (int row = 0; row < 3; row++)
                {
                    sb.Append("    ")
                        .Append(m[row * 3].ToString("F4", CultureInfo.InvariantCulture)).Append("  ")
                        .Append(m[row * 3 + 1].ToString("F4", CultureInfo.InvariantCulture)).Append("  ")
                        .AppendLine(m[row * 3 + 2].ToString("F4", CultureInfo.InvariantCulture));
                }
            }

            sb.Append("  デモザイク: ").AppendLine(choice.ApplyDemosaic ? "適用 (バイリニア)" : "なし");

            // 同名の .txt に利用者のファイル(撮影メモなど)や別の画像の付随テキストがあれば上書きせず別名へ書く。
            // 書き出し先の確認と書き込みは UI スレッドの外で行う
            string text = sb.ToString();
            return await Task.Run(() => ProcessingSidecar.Write(imagePath, text));
        }
        catch (Exception)
        {
            // 付随情報の保存失敗は本体の保存結果に影響させない(保存完了の表示で知らせる)
            return " / 付随テキストを保存できませんでした";
        }
    }

    // ---- バッチ現像 / 動画書き出し ----

    private void OnBatchExportClick(object sender, RoutedEventArgs e)
    {
        if (_currentImage is null || _currentFormat is null || _currentPath is null)
        {
            return;
        }

        // 補正・HDR派生を適用した画像はディスク上のファイルと一致しないため、
        // ファイル単位で読み直すバッチの対象にしない
        if (_derivedImage is not null || _correctionLabel is not null)
        {
            MessageBox.Show(this,
                "補正・HDR表示を適用中はバッチ書き出しできません。元のファイルを開き直してください。",
                "バッチ書き出し", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (RejectWhileImageReplacing("バッチ書き出し"))
        {
            return;
        }

        using BusyScope busy = EnterBusy();
        bool rawTargets = IsRawFile(_currentPath);
        RawFormat format = _currentFormat;
        IReadOnlyList<string> targets;
        if (rawTargets)
        {
            // 読み込んだときに UI スレッドの外で取ったサイズ(ここで FileInfo を撃つと切断した NAS で固まる)
            long size = _currentFileSize;
            if (size <= 0)
            {
                MessageBox.Show(this, "対象ファイルのサイズを取得できませんでした。",
                    "バッチ書き出し", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 連番の候補は表示中のファイルのフォルダから選ぶ(左パネルの一覧が別のフォルダでも、そのフォルダの
            // ファイルを書き出さない)。連番を決められなければ TIFF 等と同じく開いているファイル1件だけを対象にする
            targets = SequenceScanner.FindStack(_currentPath, size, CandidateFiles());
            if (targets.Count == 0)
            {
                targets = new[] { _currentPath };
            }
        }
        else if (_tiffStack is { PageNavigationEnabled: true })
        {
            targets = new[] { _currentPath };
        }
        else
        {
            // TIFF等は連番の命名で対象を決める(再生パネルと同じ規則)。
            // 連番でなければ開いているファイル1件だけを対象にする
            targets = SequenceScanner.FindNumberedStack(_currentPath, CandidateFiles());
            if (targets.Count == 0)
            {
                targets = new[] { _currentPath };
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        string folder = Path.GetDirectoryName(_currentPath)!;
        var dialog = new BatchExportDialog(
            targets.Count, Path.Combine(folder, "export"), rawTargets,
            _currentFormat?.Width ?? 0, _currentFormat?.Height ?? 0,
            _tiffStack is { PageNavigationEnabled: true } stack ? stack.PageCount : 1,
            sourceFolder: folder)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        BatchChoice choice = dialog.Result;
        // バッチも1フレーム単位で現像するため、判定はフレーム画素数で行う。開始前は表示中の画像で判定し、
        // 寸法の異なるページ・連番のファイルは書き出しの実行中に1枚ごとに判定して中止する(BatchFrameRenderer)
        if (!BatchTiffOutput.IsTiff(choice.Format)
            && (long)format.Width * format.Height > RawLoader.DefaultInMemoryPixelThreshold)
        {
            MessageBox.Show(this, "1億画素を超える画像の現像バッチはサポートされていません(TIFF16は可)。",
                "バッチ書き出し", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExecuteBatch(targets, format, choice);
    }

    private void ExecuteBatch(
        IReadOnlyList<string> targets, RawFormat format, BatchChoice choice)
    {
        // 「表示調整を焼き込む」オフなら黒/白点・ゲイン・ガンマ・コントラストを
        // ニュートラルにする。WB・マトリクス・デモザイクの現像段は残す
        // (どう写っているかの色は保ちつつ、見え方の調整だけ外す)。既定の白点はファイル・ページの
        // ビット深度で決まるので、表示LUTと現像の白点は BatchFrameRenderer が1枚ごとに選ぶ(lut = null)
        DisplayLut? lut = choice.ApplyDisplayLut ? BuildLut() : null;
        DevelopParameters developParameters = CurrentDevelopParameters();
        if (!choice.ApplyDisplayLut)
        {
            developParameters = developParameters with
            {
                BlackLevel = 0,
                Gain = 1.0,
                Gamma = 1.0,
                Contrast = 1.0,
            };
        }

        // 現像LUTはページ・ファイルごとのビット深度で作る(白飛びの判定がビット深度で決まる)。
        // Bayer は表示中の画像ではなく引き継いでいる指定から取る(カラーのページの表示中でもグレーのページを現像する)
        BayerPattern batchBayer = ImageFileBayer.ForBatch(
            IsRawFile(targets[0]), format.Bayer, _tiffStack, _sequenceBayerOverride);
        var renderer = new BatchFrameRenderer(batchBayer, developParameters, lut);
        int width = format.Width;
        int height = format.Height;
        bool video = choice.Format is BatchFormat.AviMjpeg or BatchFormat.Mp4H264;
        string videoPath = Path.Combine(
            choice.OutputFolder,
            Path.GetFileNameWithoutExtension(targets[0])
                + (choice.Format == BatchFormat.Mp4H264 ? "_seq.mp4" : "_seq.avi"));

        // 動画は一時ファイルへ書き切ってから置換する。最終パスへ直接書くと、
        // 失敗・キャンセル時に上書き対象だった既存の動画を失う
        string videoTempPath = OutputPaths.BuildPartialPath(videoPath);

        // 出力先に元のフォルダを選ぶと、同じ拡張子の1枚ものの元画像と同じ名前になる。元画像を黙って
        // 焼き込み結果へ置き換えないよう、元ファイルと同じパスへは書かずに中止する
        var sourceGuard = new BatchSourceGuard(targets);

        // 中断・エラー時は動画の一時ファイルだけを片付ける(最終パスは書き切るまで触っていないので、同名の既存動画は
        // そのまま残る)。片付けも書き出しと同じく UI スレッドの外で行う。以前は進捗表示を閉じた後に UI スレッドで
        // File.Exists と削除を行い、出力先(既定は元画像のそばで NAS のことが多い)の切断で失敗したときに固まった
        ProgressWindow result = ProgressWindow.Run(
            this,
            $"バッチ書き出し中 ({targets.Count}件)",
            (progress, ct) => Task.Run(() => OutputPaths.RunDeletingPartialOnFailure(video ? videoTempPath : null, () =>
            {
                Directory.CreateDirectory(choice.OutputFolder);
                int jpegQuality = VideoQualitySettings.JpegQuality(choice.Quality);
                AviMjpegWriter? avi = null;
                Mp4H264Writer? mp4 = null;
                try
                {
                    if (choice.Format == BatchFormat.AviMjpeg)
                    {
                        avi = new AviMjpegWriter(videoTempPath, width, height, choice.Fps);
                    }
                    else if (choice.Format == BatchFormat.Mp4H264)
                    {
                        mp4 = new Mp4H264Writer(
                            videoTempPath, width, height, choice.Fps,
                            VideoQualitySettings.EncoderQuality(choice.Quality),
                            VideoQualitySettings.BitsPerPixel(choice.Quality));
                    }

                    for (int i = 0; i < targets.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string file = targets[i];
                        // RAWの全フレーム／TIFFの全ページを1枚ずつ処理する。
                        // 列挙子が画像を所有するので、中断・例外でも確実に解放される。
                        foreach (FileFrame entry in FileFrameReader.Read(file, format, ct))
                        {
                            RawImage image = entry.Image;

                            // PNG/JPEG・動画は1枚ごとに1億画素を超えないか確かめ、超えたらどのファイルの何枚目かと
                            // TIFF16 なら書き出せることを示して中止する(焼き込みでも確かめるが、動画では寸法違いの
                            // 理由より先に示す)。TIFF16 は従来どおり上限なし
                            if (!BatchTiffOutput.IsTiff(choice.Format))
                            {
                                renderer.EnsureWithinPixelLimit(entry);
                            }

                            if (video && (image.Width != width || image.Height != height))
                            {
                                throw new NotSupportedException(
                                    $"{Path.GetFileName(file)} の{entry.Index + 1}枚目のサイズ " +
                                    $"({image.Width}×{image.Height}) が動画のサイズ " +
                                    $"({width}×{height}) と異なるため動画にできません。");
                            }

                            if (video)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (avi is not null)
                                {
                                    avi.AddFrame(renderer.EncodeJpeg(entry, jpegQuality, ct));
                                }
                                else
                                {
                                    mp4!.AddFrameRgb24(renderer.RenderRgb24(entry, ct));
                                }
                            }
                            else
                            {
                                ct.ThrowIfCancellationRequested();
                                bool jpeg = choice.Format == BatchFormat.Jpeg8;
                                string outputPath = OutputPaths.BatchImagePath(
                                    choice.OutputFolder, file, entry.Index, entry.Count, entry.IsTiffPage,
                                    BatchTiffOutput.IsTiff(choice.Format) ? ".tif" : jpeg ? ".jpg" : ".png");
                                sourceGuard.EnsureNotSource(outputPath);
                                if (BatchTiffOutput.IsTiff(choice.Format))
                                {
                                    BatchTiffOutput.Save(choice.Format, image, entry.Frame, outputPath, ct);
                                }
                                else
                                {
                                    renderer.Save(entry, outputPath, jpeg, ct);
                                }
                            }

                            progress.Report((i + ((entry.Index + 1) / (double)entry.Count)) / targets.Count);
                        }

                        progress.Report((double)(i + 1) / targets.Count);
                    }

                    avi?.Finish();
                    mp4?.Finish();
                }
                finally
                {
                    avi?.Dispose();
                    mp4?.Dispose();
                }

                if (video)
                {
                    // 書き切れたときだけ最終パスへ置き換える
                    File.Move(videoTempPath, videoPath, overwrite: true);
                }
            }), ct));

        if (result.Error is not null)
        {
            MessageBox.Show(this, $"バッチ書き出しに失敗しました: {result.Error.Message}",
                "バッチ書き出し", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else if (!result.WasCanceled)
        {
            _vm.ImageInfoText = video
                ? $"動画書き出し完了: {Path.GetFileName(videoPath)}"
                : $"バッチ書き出し完了: {targets.Count}件 → {choice.OutputFolder}"
                    + (BatchTiffOutput.CompletionNote(choice.Format) is { } note ? $" · {note}" : "");
        }
    }
}
