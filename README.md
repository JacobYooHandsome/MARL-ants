# MARL-ants

```sh
pip3 install torch -f https://download.pytorch.org/whl/torch_stable.html
pip3 install -e ./ml-agents/ml-agents-envs
pip3 install -e ./ml-agents/ml-agents
```

## Parallel training sweep

Build the Unity project and launch the four Ant PPO configs in parallel:

```sh
tools/build_and_train_parallel.sh ant-sweep-v1 --force
```

The script builds to `Builds/<name>.app`, then starts the Base, Team,
Reciprocal, and TeamReciprocal configs on ports `5005`, `5015`, `5025`, and
`5035` when that port block is free. If any requested port is already busy, it
shifts to the next free block before launching. Per-run logs are written to
`logs/training/<name>/`.
