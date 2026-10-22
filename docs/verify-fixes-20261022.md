# コードレビュー指摘の反映・検証記録 (2026-10-22)

対象は検証報告の #1〜#34 のうち、先に修正されていた #23 を除く33件。
ローカルの `fix/verify-findings` (`349e27b`) に既存の修正・回帰テストが揃っていたため、
差分とテストを確認し、`master` に fast-forward で取り込んだ。#34 は実装ではなく XML 文書の修正。

## 33件の確認範囲

以下のテスト名は `RawAnalyzer.Tests` 内のクラス名。全体テストで関連する既存テストも実行した。

| 指摘 | 反映した内容 | 主な確認先 |
|---|---|---|
| #1・#2 | 白点を指定コードの画素値へ合わせる。8bit の既定表示・無調整保存を恒等変換にする | DisplayLevelsTests、ImageFileSaverTests、BatchFrameRendererTests、ComparePaneTests |
| #3・#4・#7 | 画像演算を出力のコード格子へ丸め、ゲイン補正の参照平均を内部値から計算する | ImageCalculatorRoundingTests、ReferenceImageTests |
| #5 | ビニング・フィルタ・HDR 合成で素材の最大コードを16bitの65535へ写す | SixteenBitSaturationTests、ProcessedImageReplacementTests、MergedViewLevelsTests |
| #6 | WIC の8bit入力も `code << 8` に統一する | EightBitNormalizationTests、TiffStackTests、SaveTests |
| #8 | 欠陥補間値を元のビット深度のコードへ四捨五入する | DefectCorrectorTests |
| #9 | ゼブラの閾値を画像のビット深度のコードで求める | LevelOverlayTests、ZebraToolTipTests |
| #10 | ゲインやWBゲインが1未満でも、CCM経路の白飛びを中性に保つ | ColorPipelineTests、ColorAndMetricsTests |
| #11 | 行交互HDRの行位相が合わない場合、誤ったBayer表示・解析を止め、分割・合成へ案内する | HdrExposureMixTests、CursorReadoutTests、DisplayModeSelectionTests、ImageFileSaverTests |
| #12 | 射影のσを平均からの偏差で計算し、桁落ちを避ける | ImageAnalysisTests |
| #13 | 一定値領域の分散を厳密に0にする | ConstantRegionVarianceTests |
| #14 | 細長い領域では短い軸を間引かず、Bayerチャネルの脱落を避ける | ImageAnalysisTests |
| #15 | 幅1・高さ1のカラー現像をバイリニア経路で扱う | ColorPipelineTests |
| #16 | タイル数計算の桁あふれを避け、壊れた寸法を検査する | TiffSpecTests |
| #17 | CopyRegion の加算・乗算を広い整数型で検査する | RawLoaderTests |
| #18・#29 | HDR分割の大きなライン単位・オフセットを検査し、高さ不足の理由を正しく示す | HdrTests |
| #19 | 1タイル内でもキャンセルと進捗を確認する | TiffSpecTests |
| #20・#24・#26 | 壊れたUTF-16を入力エラーにし、0始まりのカンマ区切りの誤読を防ぎ、読点の区切りを扱う | NumericInputTests、VideoSettingsTests |
| #21 | 未定義の列挙値を検査し、設定の読み込み・表示・保存の解釈を揃える | RawFormatTests、FormatHistoryStoreTests、RawImportDialogTests |
| #22 | メモリ上のTIFF読み込みでも FillOrder=2 を明示的に拒否する | TiffLoaderTests |
| #25 | 全角数字の自然順ソート・連番判定を直す | NaturalOrderComparerTests、SequenceScannerTests、SequenceScannerNumberedTests |
| #27 | HDRビュー切替時に古いヒストグラムを捨て、自動コントラストのビット深度を照合する | DisplayLevelsTests、MainWindowの切替処理を確認 |
| #28 | 射影の中央値を小数2桁で表示する | ProjectionWindowTests |
| #30 | 並列処理の進捗通知を単調に増やす | ProjectionAnalysisTests |
| #31 | fps入力のエラー文に丸める前の値を表示する | VideoSettingsTests |
| #32・#33 | 負のゼロを正規化し、整数TIFFの0〜1を実数と説明しない | SampleScalingTests、WideSampleTiffTests |
| #34 | 比較器の戻り値の文書を「負・0・正」に直す | NaturalOrderComparer の XML コメントを確認 |

## 統合時に追加した修正

- #11の制限漏れ: 一括PNG/JPEG・動画出力でもBayer行位相を確認する。自動WBとWBスポイトも分割・合成ビューへ案内する。一括現像4ケースは修正前の失敗、修正後の成功を確認した。
- ビット深度の違うページ・ファイルへ送る際、黒0・白最大の既定値は送り先へ合わせる。調整済みのレベルは内部値を保持する。8bit→16bit→8bitの表示と、調整済みの値の保持をテストした。
- HDR合成の黒レベルが元素材の最大コード値以上なら、フルスケールが0以下になる前に入力エラーにする。8/12/16bitの5ケースは修正前の失敗、修正後の成功を確認した。
- .NET SDK 10 / C# latest でもテストをビルドできるよう、配列の `Reverse()` 2箇所を `Enumerable.Reverse(...)` と明示した。

## 検証

実行環境: Windows、.NET SDK 10.0.301、対象フレームワーク .NET 8 / WPF。

```powershell
dotnet build RawAnalyzer.sln -c Release --no-restore
dotnet test RawAnalyzer.sln -c Release --no-build --no-restore --logger "trx;LogFileName=verify-findings-final-20261022.trx" --results-directory artifacts/TestResults
```

追加修正後の全体テストは **1,906件成功・0件失敗・2件スキップ（合計1,908件）**。
結果は `artifacts/TestResults/verify-findings-final-20261022.trx`。

スキップは従来のサンプル依存テスト2件。指定ファイルがないため実行していない。

- `GigapixelSmokeTests` (`RAWANALYZER_GIGAPIXEL_PATH`)
- `DolSampleSmokeTests` (`RAWANALYZER_DOL_SAMPLE_PATH`)

目視による画面操作の確認は今回実施していない。ローカルのコードとReleaseビルドが対象で、GitHubへのpush・リリース公開は別作業。
