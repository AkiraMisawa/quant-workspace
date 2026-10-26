# Claude Codeへの引き継ぎ資料

作成日：2026年10月24日。仮称：Quant Workbench。

## 今回作るもの

中心は、クオンツ機能を組み合わせる **Capability Composition** と、入力・中間結果・最終結果を柔軟に表示する **UI Composition** の基盤です。Capability、Workflow、View、Contextを独立させ、XVAプライサーと金利ティッキングプライサーを最初の固定アプリとして実装します。AIは同じ機能を呼び、SIMM比較などの分析を組み立てます。

初期クライアントは **React + 実際のExGrid（Reactアダプター）**（2026-10-26、`docs/adr/0001-the-client-is-react-with-ex-grid.md`。当初のBlazor + MudBlazorを置き換え）。個人環境の計算エンジン・データ取得先を、会社環境のアダプターへ交換できる設計です。

## 使い方

1. ZIPを展開します。
2. 新しいリポジトリのルートに内容を置きます。既存リポジトリの場合は、既存の`CLAUDE.md`を上書きせず内容を統合します。
3. Claude Codeをそのリポジトリで起動します。
4. `prompts/01-start.md`の内容を最初のメッセージとして渡します。
5. 実装後のレビューには`prompts/02-review.md`、会社環境への接続には`prompts/03-company-integration.md`を使います。

閲覧用の`INDEX.html`と`MANIFEST.sha256`は元のZIPにのみ含まれ、このリポジトリには入れていません（文書を更新すると内容がずれるため）。

## 最初に読むファイル

| ファイル | 役割 |
|---|---|
| `CLAUDE.md` | 常に守る実装ルールと資料の読み順 |
| `START_HERE.md` | プロジェクトの目的・範囲・使い方 |
| `docs/00-decisions.md` | 確定事項、推奨初期値、旧案からの変更 |
| `docs/02-composition-architecture.md` | Capability／Workflow／View／Contextの分離 |
| `docs/03-xva-domain.md`、`04-client-state.md` | XVAの業務ルールとクライアントの状態管理 |
| `docs/05-execution-history.md`、`06-persistence-migration.md` | 実行ID、履歴、DB、ZIP、旧データの扱い |
| `docs/07-rates-live.md`、`08-provider-boundaries.md` | ティッキングと会社用アナリティクスへの交換 |
| `docs/09-ai-and-views.md` | AIの連携と宣言的な動的ビュー |
| `docs/11-acceptance.md`、`12-implementation-plan.md` | 実装の完了条件と進める順序 |
| `contracts/`、`examples/` | 型・DBのたたき台と整合するサンプル |

## 特に誤解してほしくない決定

- XVAでは、Price時の確定・検証済みの画面入力が計算の正です。サーバがデフォルトや最新データで再上書きしません。
- CVA Recoveryが空なら空のまま渡し、アナリティクスがCDS Recoveryを使用します。無担保CSAは`nocsa_{ma id}`、デフォルトの無担保割引カーブは設定可能です（例：`jpy_ois`）。
- Original、ユーザー編集の記録、最終入力、アナリティクス用ZIPは、それぞれ目的が異なります。
- 1回の業務依頼を1つのRunとして、その下に複数Caseと計算依頼を持たせます。Before／Afterは独立したアナリティクス依頼です。
- ドラフトトレードはRun作成前から存在し、同じワークスペースの次のRunでも再利用できます。
- ライブの毎ティックにRunを作りません。明示的な保存・キャプチャで履歴化します。
- AIは固定ページを操作せず、同じアプリケーションサービスを呼びます。
- 汎用OSS候補は合成基盤です。XVA固有処理、会社の権限・モデル規則、社内SDKは分離します。
- 会社に既にある複数NSの分割・HPC実行・集約を再実装する前提にしません。

## 確定していない点

メイン変更後の手動上書き保持ルール、項目別Before／After編集範囲、XCCYの商品範囲、Agent SDK、会社の無人認証、実際のExGrid API・ブラウザ対応などは、明確に未確定と記載しました。これらを理由に全作業を止めず、影響する実装段階で確認します。

## この資料の検証範囲

これは実装用の資料一式です。アプリケーション本体は未実装です。JSON Schema・サンプルの整合、SQLスキーマの実行、文書リンク、HTMLの表示を検証し、その結果を`VALIDATION.md`に記録しています。実際の金融計算、会社接続、性能、実機のExGridタッチ操作を検証済みとするものではありません。
