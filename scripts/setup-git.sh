#!/bin/sh
# clone 后运行一次：只为本仓库配置 GitHub 身份与 SSH，不修改全局 git / ~/.ssh 配置。
#   ./scripts/setup-git.sh
#   GIT_NAME=xxx GIT_EMAIL=123+xxx@users.noreply.github.com SSH_KEY=~/.ssh/id_github ./scripts/setup-git.sh
set -eu

cd "$(git rev-parse --show-toplevel)"

GIT_NAME="${GIT_NAME:-CoderLeoD}"
GIT_EMAIL="${GIT_EMAIL:-19791438+CoderLeoD@users.noreply.github.com}"
SSH_KEY="${SSH_KEY:-$HOME/.ssh/id_ed25519}"

case "$GIT_EMAIL" in
    *@users.noreply.github.com) ;;
    *) echo "GIT_EMAIL 必须是 GitHub 隐藏邮箱（xxx@users.noreply.github.com）" >&2; exit 1 ;;
esac

echo "==> 提交身份：$GIT_NAME <$GIT_EMAIL>"
git config user.name "$GIT_NAME"
git config user.email "$GIT_EMAIL"

echo "==> 启用仓库自带钩子（.githooks：提交/推送前检查邮箱）"
git config core.hooksPath .githooks
chmod +x .githooks/* 2>/dev/null || true

# GitHub 官方公布的 ED25519 主机公钥，写到 .git 里，不动 ~/.ssh/known_hosts
# 指纹 SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU
# 见 https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/githubs-ssh-key-fingerprints
KNOWN_HOSTS="$(git rev-parse --absolute-git-dir)/github_known_hosts"
echo "github.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl" > "$KNOWN_HOSTS"

if [ -f "$SSH_KEY" ]; then
    echo "==> SSH 密钥：$SSH_KEY"
    git config core.sshCommand "ssh -i \"$SSH_KEY\" -o IdentitiesOnly=yes -o IdentityAgent=none -o UserKnownHostsFile=\"$KNOWN_HOSTS\" -o StrictHostKeyChecking=yes"
else
    echo "==> 未找到 $SSH_KEY，使用系统默认 SSH 配置（可用 SSH_KEY=... 指定）"
    git config --unset core.sshCommand 2>/dev/null || true
fi

echo "==> 检查已有提交"
bad=$(git log --all --format='%ae%n%ce' 2>/dev/null | sort -u | grep -v '@users.noreply.github.com$' || true)
if [ -n "$bad" ]; then
    echo "⚠️  已有提交中包含非隐藏邮箱：" >&2
    echo "$bad" | sed 's/^/     /' >&2
else
    echo "    全部正常"
fi

echo "完成。"
