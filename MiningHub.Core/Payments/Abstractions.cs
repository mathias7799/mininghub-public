﻿using System.Data;
using System.Threading.Tasks;
using MiningHub.Core.Configuration;
using MiningHub.Core.Persistence.Model;

namespace MiningHub.Core.Payments
{
    public interface IPayoutHandler
    {
        Task ConfigureAsync(ClusterConfig clusterConfig, PoolConfig poolConfig);

        Task<Block[]> ClassifyBlocksAsync(Block[] blocks);
        Task CalculateBlockEffortAsync(Block block, double accumulatedBlockShareDiff);
        Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, Block block, PoolConfig pool);
        Task PayoutAsync(Balance[] balances);

        string FormatAmount(decimal amount);
    }

    public interface IPayoutScheme
    {
        Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, PoolConfig poolConfig,
            IPayoutHandler payoutHandler, Block block, decimal blockReward);
    }
}