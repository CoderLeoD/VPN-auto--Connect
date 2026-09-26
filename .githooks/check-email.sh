#!/bin/sh
# 公共检查：只允许 GitHub 隐藏邮箱（xxx@users.noreply.github.com）出现在提交的作者/提交者中，
# 防止把工作邮箱等真实邮箱带到 GitHub 上。
#   check-email.sh <邮箱>...   任一不合规则返回 1

ALLOWED_SUFFIX="@users.noreply.github.com"

bad=0
for email in "$@"; do
    case "$email" in
        *"$ALLOWED_SUFFIX") ;;
        *) echo "  ✗ $email" >&2; bad=1 ;;
    esac
done
exit $bad
