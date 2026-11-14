# input-pedometer
キーボードやマウス操作の万歩計

## 動作環境
 
- Windows 10（1703以降）/ Windows 11
- .NET Framework 4.x


## ビルド
 
`build.bat` を実行

```
build.bat
```

または、Windowsに標準で入っているコンパイラで直接ビルド
 
```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:InputPedometer.exe InputPedometer.cs
```

## 自動起動

画面の「Windows起動時に自動で起動」にチェックを入れると、PC起動時に最小化した状態で起動。
（`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` に登録。管理者権限は不要）

exe を移動した場合は、移動後に一度起動すれば登録先のパスも更新される。

