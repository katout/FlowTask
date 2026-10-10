# Security policy

## Maintainer

katout maintains this library and is responsible for handling security reports.

## Supported versions

Until 1.0, only the latest published version receives fixes. A fix ships as a new version; see CHANGELOG.md.

## Reporting a vulnerability

Please report privately through GitHub's private vulnerability reporting: open the repository's **Security** tab and
choose **Report a vulnerability**. Do not open a public issue, pull request or discussion for a vulnerability.

Include what you can of the following:

- the affected package and version (or commit);
- the engine or runtime (Unity version and scripting backend, Godot version, .NET version);
- steps or a minimal project that reproduces the problem, and its impact.

Reports are handled on a best-effort basis. You will be told whether the report is accepted, and credited in the
advisory unless you ask not to be.

Vulnerabilities in dependencies (UniTask, R3, GodotSharp, the .NET runtime, Unity) belong to their own projects; report
them there. If one of them affects how this library must be used, a report here is welcome too.

---

# セキュリティ方針

## 保守の責任者

このライブラリは katout が保守しており、セキュリティの報告への対応に責任を持ちます。

## 対象の版

1.0 までは、公開した最新の版だけを修正します。修正は新しい版として公開します（CHANGELOG.md）。

## 脆弱性の報告

GitHub の private vulnerability reporting を使って、非公開で報告してください。リポジトリの **Security** タブを開き、**Report a vulnerability** を選びます。脆弱性については、公開の issue、プルリクエスト、ディスカッションを作らないでください。

分かる範囲で、次のことを書いてください。

- 該当するパッケージと版（またはコミット）
- エンジンやランタイム（Unity の版とスクリプトバックエンド、Godot の版、.NET の版）
- 再現の手順か最小のプロジェクトと、影響

対応はできる範囲で行います。報告を受け付けるかどうかをお知らせし、希望されない場合を除いて、公開する勧告に報告者のお名前を載せます。

依存するもの（UniTask、R3、GodotSharp、.NET ランタイム、Unity）の脆弱性は、それぞれのプロジェクトに報告してください。それがこのライブラリの使い方に影響する場合は、こちらへの報告も歓迎します。
