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

