# DC Folder Bridge

Explorerで開かれた通常のフォルダーをDouble Commanderへ渡す、Windows用の常駐ツールです。レジストリの書き換え、管理者としての実行、自動起動の登録は行いません。起動中だけ動作します。

## 使い方

1. このフォルダーを、書き込み可能な場所に配置します。
2. `settings.ini` の `DoubleCommander=` を、使用する `doublecmd.exe` の場所に変更します。初期値は `doublecmd.exe` で、本ツールと同じフォルダーにある実行ファイルを指します。パスを引用符で囲む必要はありません。本ツールからの相対パスも指定できます。Double Commander本体は同梱していません。
3. `DCFolderBridge.exe` をダブルクリックします。タスクトレイにアイコンが表示されます（隠れているアイコンの中も確認してください）。
4. フォルダーを開くと、DCの左側の新しいタブへ渡します。起動済みのDCには `-C -P L -T` で渡します。
5. アイコンを右クリックして「一時停止」または「終了」を選びます。終了すると、このツールによる転送は止まります。

設定変更後はツールを終了して再起動してください。二重起動は防止しています。コマンドから終了する場合は `DCFolderBridge.exe --stop` を使えます。

他のツールで設定したExplorerの関連付けは変更しません。既に別の置換設定がある場合は、本ツールの終了後もその設定が適用されます。

## 動作と保護条件

- 500ミリ秒間隔でShell.Applicationのウィンドウ一覧を調べます。通常のファイルシステムフォルダーを対象とし、同じパスが3回続いてから渡します。Explorerの表示から転送までは通常1～2秒程度です。
- 起動時・一時停止解除時に既に開かれていたフォルダーは転送しません。その後にパスが変わった場合は転送しますが、その既存ウィンドウは閉じません。
- 新たに開かれたExplorerは、同じパスのまま、単一のShellウィンドウ・単一のShellTabWindowClassと判定でき、DCが前面になった場合だけ閉じます。explorer.exeプロセス自体は終了しません。
- DCを前面に出す操作を1回試みます。転送後5秒以内に条件が成立しなければExplorerを残します。
- DCへの引数送信には「対象フォルダーを開き終えた」という応答がありません。前面表示は厳密な受信確認ではありません。DC側にエラーが出る環境では `CloseExplorer=false` にしてください。
- 同じウィンドウハンドルに複数のShellオブジェクトがある場合は転送も見送ります。Windows 11の既存タブ再利用・複数タブは全面対応ではありません。Windows更新によりウィンドウ構造が変わる可能性もあります。
- ごみ箱、PC、検索結果などの仮想フォルダーや、アプリの「開く」「名前を付けて保存」ダイアログは対象外です。
- 通常のUNC共有パスは判定対象です。ネットワークの存在確認を監視処理に入れず、接続先へのアクセスはDCに任せます。共有先ごとの動作確認は必要です。
- 転送に失敗したパスは自動で連続再試行しません。別フォルダーへ移動するか、フォルダーを開き直してください。

## 設定

```ini
DoubleCommander=doublecmd.exe
CloseExplorer=true
DryRun=false
```

- `CloseExplorer=false`: フォルダーをDCへ渡してもExplorerを閉じません。
- `DryRun=true`: 検知のログだけを記録します。DC起動・Explorer終了はしません。
- `bridge.log`: 起動、停止、転送、終了、エラー種別を記録します。転送先のフォルダーパスは記録しません。256KBを超えると古いログを別名で残します。

同じユーザー・通常権限でExplorerとDCを動かす構成を想定しています。組織の管理下にある端末では、その組織のソフトウェア利用規定に従ってください。

## 動作確認の範囲

- .NET FrameworkのC#コンパイラーでビルド成功。
- パスの判定（通常パス、日本語、UNC、仮想パスなど）、引数の引用処理、初回の除外、パス変更と重複防止、終了条件の自己テスト成功。
- ローカル環境で、日本語・空白・記号を含むフォルダーの起動引数送信と、転送対象の単一タブExplorerの終了を確認。
- 起動時に既に開いているテスト用フォルダーを転送・終了しないことを確認。
- 終了コマンドで常駐プロセスが終了することを確認。
- Windows 11の複数タブ、各アプリ固有のフォルダー呼び出し、ネットワーク共有は実機検証の対象外です。すべての環境で同じ動作を保証するものではありません。

## 配布物とログ

配布物は `DCFolderBridge.exe`、`settings.ini`、`README.md`、`Bridge.cs`、`build.cmd` の5ファイルです。実行ログやDouble Commander本体は含みません。

利用後のフォルダーを再配布する場合は、`settings.ini` に記入した個人用のパスを初期値へ戻し、`bridge.log` と過去のログ（`bridge.log.*.old`）を配布物から除いてください。

## ソースとビルド

`Bridge.cs` に全ソースを同梱しています。`build.cmd` で64ビットのWindows Forms EXEを作れます。Windowsの.NET Framework 4.xを使用し、追加NuGetパッケージはありません。

自己テストは `DCFolderBridge.exe --self-test "結果ファイルの絶対パス"` で実行できます。結果ファイルにPASSまたはFAILを書き込みます。

仕組みの参考:
- https://tablacus.github.io/wiki/addons/openinstead.html
- https://raw.githubusercontent.com/tablacus/TablacusExplorerAddons/master/openinstead/script.js
- https://doublecmd.github.io/doc/en/commandline.html

Tablacusのコードを組み込むことなく、WindowsのShell Automation APIで独立して実装しています。
