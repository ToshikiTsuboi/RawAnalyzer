# 計算ロジック検証と修正の記録 (2026-10)

2026-10-18 に Core と App の非 WPF 部分の計算を、独立に書いた参照実装(総当たり・numpy・厳密な有理数計算)と突き合わせて検証した。
見つかった不具合は 34 件(中 7・低 27・高 0)、反証は 1 件(DR の分子に黒レベルを引かないのは仕様どおり)。
34 件はすべて修正済みで、主な修正には回帰テストがある(#34 は XML コメントの修正のみ)。2026-10-22 に `master` へ取り込んだ。

重要度: 高 = 通常操作で計測値を誤る・落ちる / 中 = ありうる条件で誤る / 低 = まれな入力・表示・文言のみ。

| # | 重 | 内容 → 修正 | 主な回帰テスト (`〜Tests`) |
|---|---|---|---|
| 1・2 | 中・低 | 白レベルに指定したコードの画素が 255 にならない、8bit の既定表示・無調整保存が恒等にならない → 白点をコードの区間に合わせる | DisplayLevels, ImageFileSaver, BatchFrameRenderer |
| 3・4・7 | 中・低 | 画像演算の結果が端数を持ち統計・raw 保存で平均が偏る → 出力のコード格子へ丸め、参照平均を内部値から計算 | ImageCalculatorRounding, ReferenceImage |
| 5 | 中 | ビニング・フィルタ・HDR 合成の 16bit 化で白飛びが 65535 に届かない → 素材の最大コードを 65535 へ写す | SixteenBitSaturation, MergedViewLevels |
| 6 | 中 | WIC の 8bit だけ v×257 で他経路(v<<8)と食い違う → `code << 8` に統一 | EightBitNormalization, TiffStack |
| 8 | 低 | 欠陥補正値が端数を持ち切り捨てられる → 元のビット深度のコードへ四捨五入 | DefectCorrector |
| 9 | 低 | ゼブラの飽和しきい値が 8/10bit で ≥98% と食い違う → 画像のビット深度のコードで判定 | LevelOverlay, ZebraToolTip |
| 10 | 中 | ゲイン・WB ゲイン < 1 で CCM 経路の白飛びが色付く → 白飛びを中性に保つ | ColorPipeline |
| 11 | 中 | 行交互 HDR の行位相が奇数のとき Raw 表示の Bayer 色・チャネルを取り違える → 該当機能を止め分割・合成ビューへ案内 | HdrExposureMix, CursorReadout, DisplayModeSelection |
| 12・13 | 低 | 射影の σ の桁落ち、一定値領域の分散が 0 にならない → 偏差で計算、厳密に 0 | ImageAnalysis, ConstantRegionVariance |
| 14 | 低 | 細長い領域の間引きで Bayer チャネルが脱落 → 短い軸を間引かない | ImageAnalysis |
| 15 | 低 | 幅 1・高さ 1 のカラー現像で範囲外読み出し → バイリニア経路で処理 | ColorPipeline |
| 16・17・18・29 | 低 | タイル数・CopyRegion・HDR ライン単位の int 桁あふれ、高さ不足の誤った理由 → 広い整数型で検査 | TiffSpec, RawLoader, Hdr |
| 19 | 低 | 1 タイル画像でキャンセル・進捗が効かない → タイル内でも確認 | TiffSpec |
| 20・24・26・31 | 低 | 壊れた UTF-16、"0,125" の誤読、全角読点、fps エラー文の丸め → 入力解釈を修正 | NumericInput, VideoSettings |
| 21 | 低 | 未定義の列挙値が設定から入る → 読み込み・表示・保存で検査 | RawFormat, FormatHistoryStore |
| 22 | 低 | メモリ上の TIFF 読み込みで FillOrder=2 を見逃す → 明示的に拒否 | TiffLoader |
| 23 | 低 | 横軸の手動最大 ≥ 2^31 で線が消える → int 変換前に範囲へ収める (`518f975`) | ProfilePlotNavigation |
| 25 | 低 | 全角数字の自然順ソート・連番判定 → 数値として比較 | NaturalOrderComparer (AviMjpegWriterTests.cs 内), SequenceScanner |
| 27 | 低 | HDR ビュー切替直後の自動コントラストが古いヒストグラムを使う → 切替で破棄しビット深度を照合 | DisplayLevels |
| 28・30 | 低 | 射影の中央値が小数 1 桁、並列の進捗が逆戻り → 小数 2 桁、単調増加 | ProjectionWindow, ProjectionAnalysis |
| 32・33 | 低 | 値の対応の説明で -0、整数 TIFF を実数扱い → 正規化と文言修正 | SampleScaling, WideSampleTiff |
| 34 | 低 | 比較器の XML コメントが戻り値を -1/0/1 と記載 → 「負・0・正」に修正 | — |

取り込み時の追加修正: 一括 PNG/JPEG・動画出力と自動 WB でも #11 の行位相を確認、ビット深度の違う画像へ送るときの既定レベルを送り先に合わせる、
HDR 合成の黒レベルが最大コード以上なら入力エラーにする。

**未検証の範囲**: WPF/WIC に依存する描画・ダイアログ・WIC 経路(Linux で実施したため)、実寸 10 億画素の MMF 読み込み(しきい値を下げて代替)。
外部サンプル依存の `GigapixelSmokeTests`・`DolSampleSmokeTests` は環境変数でファイルを指定したときだけ動く。
