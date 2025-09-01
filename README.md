# ImgProcessWpfApp

C# / WPF で作った **ヘッダ無し RAW/BIN 画像の簡易現像ビューア**です。  
Bayer(RGGB) のデモザイク、RGBゲイン(ホワイトバランス)、カラーマトリクス、飽和クリップ、**ガンマ/コントラスト**、  
比較表示（Before/After）と **一括書き出し（PNG/JPEG/TIFF/BMP）** 等を実行できます。

---

## ✨ 主な機能

- **フォルダブラウズ**: 左のツリー + ファイル一覧、アドレスバーにパス貼り付け可
- **RAW読込**: サイズ(Width/Height)、ヘッダー(skip bytes)、ファイルビット長(8/16/32)、有効ビット(8/10/12/14/16)、  
  **上詰め(MSB)/下詰め(LSB)**、**Endianness(Little/Big)**  
- **デモザイク**: Bilinear（Bicubic は今は同等動作のプレースホルダ）
- **現像**: RGB ゲイン、任意 3×3 カラーマトリクス、飽和白飛び、**ガンマ(0.10–3.00)**、**コントラスト(-100%～+100%)**
- **比較表示**: 左=Before(ニュートラル)、右=After(設定適用)  
- **ズーム**: Ctrl+ホイール（ポインタ中心）、100%、Fit、ステータスバーのボタン
- **書き出し**: 1枚保存 / 一括保存（PNG/JPEG/TIFF/BMP、JPEG品質設定）
- **設定の永続化**: 直前の **サイズ/ヘッダー** を次回起動時に復元  
  - 保存場所: `\ImgProcessWpfApp\prefs.json`

**10/12bit のバイト境界をまたぐパック形式**（例 5B=4pix 等）は未対応です。

---

## 🖥️ 開発環境

- **Windows 10/11**
- **.NET 8 (Windows)**（7 でも可）
- **Visual Studio 2022**（.NET デスクトップ開発）


---

## ▶️ 使い方

1. 左のツリーでフォルダ選択（またはアドレスバーに `C:\path\to\data` を貼り付けて Enter）
2. 下の *Files* でファイル選択  
   - RAW の場合は **Size / Header / Bits / Align / Endian** を合わせてください
3. 右の *Develop (RAW)* でゲイン/マトリクス/ガンマ/コントラスト等を調整  
   - **Live preview** をONにすると変更が即反映
4. **Compare** トグルで Before/After を並べて確認
5. **Export Current / Batch Export** で書き出し（JPEG は品質調整可）

---

## ⌨️ ショートカット

- **Ctrl + マウスホイール**: ズーム（ポインタ中心）
- **Ctrl + 0**: 100%
- **Alt + ← / →**: フォルダ履歴の戻る/進む

---

## 📝 ライセンス

MIT License (c) 2025

