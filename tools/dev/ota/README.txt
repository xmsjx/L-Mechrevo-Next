机械革命控制台 更新通道查询工具（只读）
================================================

作用
  - 从本机读取机型信息，拼出官方更新服务器的查询地址，查三条通道：
        BIOS        /biosUp/<项目族>/<机器码>/<项目族><机器码>.json
        控制中心    /APup/<appType>/<机器码>/APUpdate.json
        VBIOS       /vbiosUp/<项目族>/<显卡PCI设备号>.json
  - 拿回清单后与本机版本比对，告诉你有没有更新，并可下载更新包到本目录。
  - 下载完只做 SHA256 校验，**脚本不会运行、也不会打开下载到的任何 exe**。

机器码怎么来的
  不是哈希。读的是主板固件清单（SMBIOS Type 45）里 BIOS 条目的清单 ID 首字段：
      51821B7E-CFAB-5182-A73F-50DAABD64376  ->  51821b7e
  每台机器读自己的，不需要官方程序参与，也不需要管理员权限。

怎么用
  1) 双击 ota-check.cmd  ->  进入菜单，按数字选：
         1  查询并下载 BIOS
         2  查询并下载 控制中心
         3  查询并下载 VBIOS
         4  列出三条通道（不下载）
         0  退出
     选中通道后：有更新会问你是否下载；已是最新会问你是否强制下载。
     下载成功后还会问一句「save a copy with .EXE extension?」——
     服务器把更新包命名成 .EX_，但内容就是完整 EXE（MZ / PE32+ / x64），
     答 y 就会在同一目录另存一份同名 .EXE（内容完全一样，只改扩展名，不会运行它）。
  2) 也可以带参数跑（不会进菜单）：
         ota-check.cmd -Menu              强制进菜单
         ota-check.cmd                    查三条通道并比对版本
         ota-check.cmd -Channel BIOS      只查一条（BIOS / CC / VBIOS）
         ota-check.cmd -Download          有更新时下载到本目录并校验 SHA256
         ota-check.cmd -Force             不管版本是否最新，直接下载当前服务端那份
         ota-check.cmd -Download -MakeExe  下载后顺手另存一份 .EXE
         ota-check.cmd -Raw               额外打印清单原文
         ota-check.cmd -OutDir D:\ota     指定下载目录（默认本目录）
         ota-check.cmd -MachineCode 12345678   手动指定机器码
         ota-check.cmd -Cm MRO -AppType 17     手动指定项目族 / appType
  3) 直接用 PowerShell 也可以：
         powershell -NoProfile -ExecutionPolicy Bypass -File .\ota-check.ps1

需要什么
  - Windows 10/11，自带 Windows PowerShell 5.1 即可；装了 pwsh 7 会自动优先用 pwsh。
  - 能联网。服务器 ota.mechrevo.com:2080 是明文 HTTP，不用改 hosts、不用代理。
  - ota-check.cmd 依赖同目录下的 ota-check.ps1 与 ota-check.json，请把整个文件夹一起拷走。

输出示例（脚本输出为英文，这是本机实测）
  local BIOS      : N.1.10MRO28
  local control   : 5.17.51.48
  machine code    : 51821b7e   (source: SMBIOS type45)   cm: MRO   appType: 17

  --- BIOS ---
    path : /biosUp/MRO/51821b7e/MRO51821b7e.json
    remote: N.1.09MRO19
    file  : /biosUp/MRO/51821b7e/GMxHGxxN109MRO19_CAP.EX_
    sha256: 0ffd0915ca9b140b4bc74ab0899608cbabe5ba054bcd86a38809a6e361af98ae
    local : N.1.10MRO28   verdict: up to date (or newer)

常见情况
  - Control center / VBIOS 显示 "no content on server (404)"：服务器那两条通道没有部署内容，
    不是你的机器有问题；等官方上线后无需改脚本就能取到。
  - 显示 "cannot read the firmware inventory id on this machine"：该机型 BIOS 没填
    SMBIOS Type 45，需要用 -MachineCode 手动指定。
  - 显示 "dir only (403)"：目录存在但不允许列出，属正常现象。
