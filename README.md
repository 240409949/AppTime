# 时间去哪儿了？
忙碌的一天匆匆而过，时间是如何分配的？是否需要管控？

# 试试这个吧
![](https://github.com/newdraw/AppTime/blob/master/files/tv.jpg) 

#### 功能：记录使用时间
每个应用和窗口的使用时间精确到秒。

![](https://github.com/newdraw/AppTime/blob/master/files/list.jpg)

#### 功能：回溯桌面
查看过往的桌面图像（可通过配置关闭）

![](https://github.com/newdraw/AppTime/blob/master/files/time.jpg)

![](https://github.com/newdraw/AppTime/blob/master/files/playback.gif)

#### 功能：分类管理
为应用或窗口加上标签，按类别统计。

![](https://github.com/newdraw/AppTime/blob/master/files/tag.jpg)
=>
![](https://github.com/newdraw/AppTime/blob/master/files/tagview.jpg)
 
# 使用方法
1、下载[Release.zip](https://github.com/newdraw/AppTime/raw/master/Release%20v0.11.zip)，解压到任意目录；

2、打开AppTime.exe;

3、注意右下角托盘图标。

![](https://github.com/newdraw/AppTime/blob/master/files/icon.jpg) 

# 升级注意
如需升级老版本的AppTime，请解压覆盖到老版本目录，但请不要覆盖data.db文件。

# 二次开发参考
开发环境Visual Studio 2019，.NET Framework 4.8。（其实不必须这么高版本，可以自行尝试降低）

发布时请把源码里的webui目录复制到exe所在目录。

# TODO List（欢迎朋友们一起完善）
1、支持多屏；

2、打开界面需要密码，截图、图标以及数据库用密码加密；

3、记录离开、屏保、锁屏等特殊情况的时间；

4、使用更现代的方式重构Web UI；

5、允许用户调整时间比例的分母为总时间或开机时间；

6、改进截图存取性能，延迟Windows关机直到截图保存完成；

7、预置常用标签，常见程序默认标签。

# 本 fork 的修改

> **来源声明**：本仓库 fork 自 [newdraw/AppTime](https://github.com/newdraw/AppTime)（作者：newdraw）。原仓库未提供 LICENSE（默认保留所有权利），本仓库的修改仅供个人学习使用；如有版权主张请与原作者联系。请勿删除原作者的署名与致谢。

基于原仓库最新 master（2024-01-23）的稳定性改进：

1. **修复"无限制"选项崩溃**：`RecordScreenDays` 为 int.MaxValue 时不再执行 `AddDays(-int.MaxValue)` 溢出（Recorder.cs）；
2. **修复锁屏/睡眠崩溃**：屏幕分辨率变为 0 时 `new Bitmap(0,0)` 抛异常导致进程退出，改为安全截屏（GetScreenSafe），屏幕不可用时跳过该帧；
3. **离开检测（不录制）**：连续无键盘/鼠标输入 5 分钟且屏幕画面静止（16x9 缩略图逐帧比对，看视频画面在动不会误判）时判定离开，停止录制和计时，恢复输入或画面变化后自动继续（Recorder.cs `AwayIdleSeconds` 可调）；
4. **进程防崩溃加固**：ffmpeg 保存线程、WebServer 请求线程、录制主循环均加异常保护，任何环节出错只记日志（save_error.log / recorder_error.log）不会带崩整个进程；
5. **内存控制**：限制同时保存的录屏缓冲数（≤2），防止 ffmpeg 保存慢时内存积压；
6. **依赖修复**：Newtonsoft.Json 从 12.0.3 升级到 13.0.2，与 packages.config 保持一致，可直接还原编译。

> 隐私说明：本仓库已移除运行时数据（data.db、images/、icons/），程序启动时会自动重建数据库。


