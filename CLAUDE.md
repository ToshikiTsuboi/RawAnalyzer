# RawAnalyzer — イメージセンサRaw画像評価アプリ

## 技術制約
- .NET 8 / C# / WPF。NuGetパッケージは一切使用禁止(テスト用xUnit系のみ可)
- TIFF/JPEG/PNGの読み書きはWPF内蔵のBitmapDecoder/BitmapEncoder(WIC)を使う
- 画像処理はすべて自前実装。Parallel.For / Span<ushort> / System.Runtime.Intrinsics を活用

## プロジェクト構成
- RawAnalyzer.Core  : UI非依存。System.Windowsを参照しない(PresentationCore等の参照禁止)
- RawAnalyzer.App   : WPF本体。MVVM(ObservableObject/RelayCommandは自前実装)
- RawAnalyzer.Tests : xUnit。CoreとApp層の純ロジック(UIを生成しないもの)が対象

## 絶対に守る性能ルール(最大10億画素=2GBを想定)
1. 全画素を必ずしもヒープに置かない。閾値(1億画素)超はMemoryMappedFileで参照
2. 表示は「ビューポート×適切な縮小ピラミッドレベル」のみ描画。全画素描画禁止
3. 16bit→8bit表示変換は65536エントリLUT。表示パラメータ変更でLUT再計算のみ
4. 重い処理はTask+CancellationToken。スライダー操作中は前ジョブ即キャンセル
5. UIスレッドをブロックするコードを書かない

## ドメイン知識
- Rawは内部的に常に16bit ushortへ正規化(下詰めNbitなら value << (16-N))
- RawFormat: 幅/高さ/ビット深度(8,10,12,14,16)/詰め(LSB/MSB)/エンディアン/
  ヘッダオフセット/フレーム数/Bayerパターン(RGGB,BGGR,GRBG,GBRG,None)/HDR方式
- HDRの格納レイアウトは「行交互」か「フレーム連結」。DOL/Staggeredといった
  ベンダー名は露光の時間関係を指すもので画素の並びからは判別できないため、
  HdrModeは並び(LineInterleaved/FrameSequential/Auto)で持つ
- ライン交互は1露光あたりの連続行数(HdrLineBlock)と段ごとの縦ずれ
  (HdrRowOffset)がセンサで異なる。分割時に整列してから合成する

## 開発ルール
- 各フェーズ完了時: dotnet build が警告ゼロ、dotnet test 全パス
- Coreのpublic APIにはXMLドキュメントコメント
- コミットはフェーズ単位で意味のあるメッセージ

## ビルド環境メモ
- .NET 8 SDK はユーザーローカル `%LOCALAPPDATA%\Microsoft\dotnet` にインストール済み
  (システムの `C:\Program Files\dotnet` は .NET 6 RC のため使用しない)
