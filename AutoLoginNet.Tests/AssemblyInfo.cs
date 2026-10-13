using Microsoft.VisualStudio.TestTools.UnitTesting;

// 部分测试会启动外部进程（schtasks / netsh），并行执行既慢又容易互相干扰
[assembly: DoNotParallelize]
