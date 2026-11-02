# -*- coding: utf-8 -*-
r"""
① 清理 installer-src\files 里确认无用的冗余（移到 backup，不直接删）
② 把 rebuild\lm_fix_build 的新构建合并进 installer-src\files
================================================================================
合并策略：只处理 WANT / SATELLITES 里由 .iss 明确引用的构建产物——files\ 里没有就补齐，
有就按内容比对后覆盖；其它文件一律不动。卫星程序集 zh-CN\ 单独处理。
用法：python merge_and_clean.py [--apply]
"""
import io
import os
import sys
import shutil
import datetime

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# 路径按本脚本位置推导（tools\ 的上一级＝仓库根），换目录或换机器都不必改。
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROOT = os.path.join(REPO, 'installer-src')
FILES = os.path.join(ROOT, 'files')
BUILD = os.path.join(REPO, 'rebuild', 'lm_fix_build')
STAMP = datetime.datetime.now().strftime('%Y%m%d_%H%M%S')
BACKUP = os.path.join(REPO, 'backup', 'files_clean_' + STAMP)

# ① 冗余清理清单（相对 files\）
JUNK = [
    r'GCU\Install-Gcu.ps1.bak',
    r'GCU\payload\30\AiStoneService\GCUBridge.InstallState',
    r'GCU\payload\30\AiStoneService\GCUBridge.InstallLog',
    r'GCU\payload\30\AiStoneService\InstallUtil.InstallLog',
    r'GCU\payload\30\AiStoneService\install.bat',
    r'GCU\payload\30\AiStoneService\uninstall.bat',
    # RESTORED 2026-10-08: r'GCU\payload\30\AiStoneService\MyControlCenter\NAudio.dll',
    # RESTORED 2026-10-08: r'GCU\payload\30\AiStoneService\MyControlCenter\SharpDX.dll',
    # RESTORED 2026-10-08: r'GCU\payload\30\AiStoneService\MyControlCenter\SharpDX.RawInput.dll',
    # 2026-10-07：GamingMonitor 已由用户手动加回 30 系载荷（GCUService.exe 会加载
    # GCUService.GamingMonitor / GamingMonitorEnable），其 Config\configs.json 是必需的配套文件
    # —— 不再列入清理清单，否则每次 --apply 都会把它移走，30GCU 包将缺少该文件。
    # （原条目：GCU\payload\30\AiStoneService\MyControlCenter\GamingMonitor\Config\configs.json）
    # RESTORED 2026-10-08: r'GCU\payload\30\AiStoneService\MyControlCenter\AppSettings\AppProfileBindingPowerSaving.json',
    # RESTORED 2026-10-08: r'GCU\payload\30\AiStoneService\MyControlCenter\AppSettings\GamingMonitor.json',
]

# ② 需要合并的构建产物（相对 publish 输出）
WANT = ['L-Mechrevo.dll', 'L-Mechrevo.exe', 'L-Mechrevo.deps.json',
        'L-Mechrevo.runtimeconfig.json', 'MechrevoLite.Core.dll']
SATELLITES = [r'zh-CN\L-Mechrevo.resources.dll',
              r'zh-CN\MechrevoLite.Core.resources.dll']
# files\ 里已存在、但 publish 不产出的文件（只保留原样，不参与合并）
KEEP = ['L-Mechrevo.dll.config', 'MechrevoLite.Core.deps.json']


def find_ci(root, rel):
    """大小写不敏感地找文件，返回真实路径或 None"""
    cur = root
    for part in rel.split('\\'):
        if not os.path.isdir(cur):
            return None
        hit = None
        for name in os.listdir(cur):
            if name.lower() == part.lower():
                hit = name
                break
        if hit is None:
            return None
        cur = os.path.join(cur, hit)
    return cur if os.path.isfile(cur) else None


def main():
    apply_ = '--apply' in sys.argv
    print("### ① 冗余清理 ###")
    moved = 0
    for rel in JUNK:
        p = find_ci(FILES, rel)
        if not p:
            print("   （已不存在）" + rel)
            continue
        size = os.path.getsize(p)
        print("   %9.1f KB  %s" % (size / 1024.0, rel))
        if apply_:
            dst = os.path.join(BACKUP, os.path.relpath(p, FILES))
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.move(p, dst)
            moved += 1
    print("   合计 %d 个文件" % len(JUNK))
    if apply_:
        # 清空目录
        for dp, dn, fn in os.walk(FILES, topdown=False):
            if not dn and not fn:
                try:
                    os.rmdir(dp)
                    print("   删除空目录 " + os.path.relpath(dp, FILES))
                except OSError:
                    pass
        print("   已移到 " + BACKUP)

    print("\n### ② 合并新构建 ###")
    merged, same, missing, added = [], [], [], []
    for rel in WANT + SATELLITES:
        src = os.path.join(BUILD, rel)
        if not os.path.isfile(src):
            missing.append(('publish 里没有', rel))
            continue
        dst = find_ci(FILES, rel)
        if dst is None:
            # WANT/SATELLITES 全部由 .iss 的 [Files] 引用，缺了打包就会失败 ⇒ 直接补齐
            dst = os.path.join(FILES, rel)
            a = os.path.getsize(src)
            print("   + 新增        %-42s %9.1f KB" % (rel, a / 1024.0))
            added.append(rel)
            if apply_:
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copy2(src, dst)
            continue
        a, b = os.path.getsize(src), os.path.getsize(dst)
        same_content = False
        if a == b:
            same_content = io.open(src, 'rb').read() == io.open(dst, 'rb').read()
        if same_content:
            same.append(rel)
            print("   = 无变化      %-42s %9.1f KB" % (rel, a / 1024.0))
            continue
        print("   → 更新        %-42s %9.1f KB -> %9.1f KB" % (rel, b / 1024.0, a / 1024.0))
        merged.append(rel)
        if apply_:
            shutil.copy2(src, dst)

    if KEEP:
        print("   仅保留（publish 不产出）：" + "、".join(KEEP))
    if missing:
        print("   跳过：")
        for why, rel in missing:
            print("     %-28s %s" % (why, rel))
    if not apply_:
        print("\n（dry-run，未改动。加 --apply 执行）")
    else:
        print("\n合并完成：更新 %d 个，新增 %d 个，未变 %d 个" % (len(merged), len(added), len(same)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
