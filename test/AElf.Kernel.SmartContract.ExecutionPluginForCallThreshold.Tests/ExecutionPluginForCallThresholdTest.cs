using System.Collections.Generic;
using System.Linq;
using Acs5;
using System.Threading.Tasks;
using AElf.Contracts.MultiToken;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.SmartContract.Application;
using AElf.Kernel.SmartContract;
using AElf.Types;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AElf.Kernel.SmartContract.ExecutionPluginForCallThreshold.Tests
{
    public class ExecutionPluginForCallThresholdTest : ExecutionPluginForCallThresholdTestBase
    {
        [Fact]
        public async Task GetPreTransactionsTest()
        {
            const long feeAmount = 10;
            
            await InitializeContracts();
            await SetThresholdFee(feeAmount);

            var plugins = Application.ServiceProvider.GetRequiredService<IEnumerable<IPreExecutionPlugin>>()
                .ToLookup(p => p.GetType()).Select(coll => coll.First());
            var plugin = plugins.SingleOrDefault(p => p.GetType() == typeof(MethodCallingThresholdPreExecutionPlugin));
            plugin.ShouldNotBeNull();
         
            var bcs = Application.ServiceProvider.GetRequiredService<IBlockchainService>();
            var chain = await bcs.GetChainAsync();
            var transactions = (await plugin.GetPreTransactionsAsync(TestContract.ContractContainer.Descriptors,
                new TransactionContext
                {
                    Transaction = new Transaction
                    {
                        From = DefaultSender,
                        To = TestContractAddress,
                        MethodName = nameof(DefaultTester.DummyMethod)
                    },
                    BlockHeight = chain.BestChainHeight + 1,
                    PreviousBlockHash = chain.BestChainHash
                })).ToList();

            transactions.ShouldNotBeEmpty();
            transactions[0].From.ShouldBe(TestContractAddress);
            transactions[0].To.ShouldBe(TokenContractAddress);
        }

        [Fact]
        public async Task CheckThreshold_Successful()
        {
            const long feeAmount = 10;
            
            await InitializeContracts();
            await SetThresholdFee(feeAmount);

            var dummy = await DefaultTester.DummyMethod.SendAsync(new Empty());
            dummy.TransactionResult.Status.ShouldBe(TransactionResultStatus.Mined);
        }

        [Fact]
        public async Task CheckThreshold_PreFailed()
        {
            const long feeAmount = 5_0000_0000L;
            
            await InitializeContracts();
            await SetThresholdFee(feeAmount);

            var originalBalance = (await TokenContractStub.GetBalance.CallAsync(new GetBalanceInput
            {
                Owner = DefaultSender,
                Symbol = "ELF"
            })).Balance;

            var targetAmount = originalBalance - feeAmount;
            var burnResult = await TokenContractStub.Burn.SendAsync(new BurnInput
            {
                Symbol = "ELF",
                Amount = targetAmount + 1
            });
            burnResult.TransactionResult.Status.ShouldBe(TransactionResultStatus.Mined);

            var dummy = await DefaultTester.DummyMethod.SendWithExceptionAsync(new Empty());
            dummy.TransactionResult.Status.ShouldBe(TransactionResultStatus.Failed);
            dummy.TransactionResult.Error.ShouldContain("Cannot meet the calling threshold.");
        }

        [Fact]
        public async Task CheckMultipleThreshold_Enough_ELF()
        {
            const long elfFeeAmount = 10;
            const long ramFeeAmount = 20;

            await InitializeContracts();
            await SetMultipleThresholdFee(elfFeeAmount, ramFeeAmount);
            
            var ramBalance = (await TokenContractStub.GetBalance.CallAsync(new GetBalanceInput
            {
                Owner = DefaultSender,
                Symbol = "WRITE"
            })).Balance;
            
            //enough ELF
            {
                var transferResult = await TokenContractStub.Transfer.SendAsync(new TransferInput
                {
                    Symbol = "WRITE",
                    To = Address.FromPublicKey(OtherTester.PublicKey),
                    Amount = ramBalance,
                    Memo = "transfer write to other user"
                });
                transferResult.TransactionResult.Status.ShouldBe(TransactionResultStatus.Mined);
                
                var dummy = await DefaultTester.DummyMethod.SendAsync(new Empty());
                dummy.TransactionResult.Status.ShouldBe(TransactionResultStatus.Mined);
            }
        }
        
        [Fact]
        public async Task CheckMultipleThreshold_Enough_RAM()
        {
            const long elfFeeAmount = 10;
            const long writeFeeAmount = 20;

            await InitializeContracts();
            await SetMultipleThresholdFee(elfFeeAmount, writeFeeAmount);
            
            var writeBalance = (await TokenContractStub.GetBalance.CallAsync(new GetBalanceInput
            {
                Owner = DefaultSender,
                Symbol = "WRITE"
            })).Balance;
            
            //enough WRITE
            {
                var transferResult = await TokenContractStub.Transfer.SendAsync(new TransferInput
                {
                    Symbol = "ELF",
                    To = Address.FromPublicKey(OtherTester.PublicKey),
                    Amount = writeBalance,
                    Memo = "transfer elf to other user"
                });
                transferResult.TransactionResult.Status.ShouldBe(TransactionResultStatus.Mined);
                
                var dummy = await DefaultTester.DummyMethod.SendAsync(new Empty());
                dummy.TransactionResult.Status.ShouldBe(TransactionResultStatus.Mined);
            }
        }
        
        [Fact]
        public async Task CheckMultipleThreshold_Failed()
        {
            const long elfFeeAmount = 1_0000_0000L;
            const long ramFeeAmount = 50_0000L;

            await InitializeContracts();
            await SetMultipleThresholdFee(elfFeeAmount, ramFeeAmount);
            
            var elfBalance = (await TokenContractStub.GetBalance.CallAsync(new GetBalanceInput
            {
                Owner = DefaultSender,
                Symbol = "ELF"
            })).Balance;
            var writeBalance = (await TokenContractStub.GetBalance.CallAsync(new GetBalanceInput
            {
                Owner = DefaultSender,
                Symbol = "WRITE"
            })).Balance;
            
            //both ELF and WRITE are not enough
            {
                var transferElfResult = await TokenContractStub.Transfer.SendAsync(new TransferInput
                {
                    Symbol = "ELF",
                    To = Address.FromPublicKey(OtherTester.PublicKey),
                    Amount = elfBalance - (elfFeeAmount - 50),
                    Memo = "transfer elf to other user"
                });
                transferElfResult.TransactionResult.Status.ShouldBe(TransactionResultStatus.Mined);
                
                var transferWriteResult = await TokenContractStub.Transfer.SendAsync(new TransferInput
                {
                    Symbol = "WRITE",
                    To = Address.FromPublicKey(OtherTester.PublicKey),
                    Amount = writeBalance - (ramFeeAmount - 50),
                    Memo = "transfer write to other user"
                });
                transferWriteResult.TransactionResult.Status.ShouldBe(TransactionResultStatus.Mined);
                
                var dummy = await DefaultTester.DummyMethod.SendWithExceptionAsync(new Empty());
                dummy.TransactionResult.Status.ShouldBe(TransactionResultStatus.Failed);
                dummy.TransactionResult.Error.ShouldContain("Cannot meet the calling threshold.");
            }
        }

        [Fact]
        public async Task GetPreTransactions_Token_CheckThreshold_Test()
        {
            var plugins = Application.ServiceProvider.GetRequiredService<IEnumerable<IPreExecutionPlugin>>()
                .ToLookup(p => p.GetType()).Select(coll => coll.First());
            var plugin = plugins.SingleOrDefault(p => p.GetType() == typeof(MethodCallingThresholdPreExecutionPlugin));
            plugin.ShouldNotBeNull();
         
            var bcs = Application.ServiceProvider.GetRequiredService<IBlockchainService>();
            var chain = await bcs.GetChainAsync();
            var transactions = (await plugin.GetPreTransactionsAsync(TestContract.ContractContainer.Descriptors,
                new TransactionContext
                {
                    Transaction = new Transaction
                    {
                        From = DefaultSender,
                        To = TokenContractAddress,
                        MethodName = nameof(TokenContractContainer.TokenContractStub.CheckThreshold)
                    },
                    BlockHeight = chain.BestChainHeight + 1,
                    PreviousBlockHash = chain.BestChainHash
                })).ToList();
            transactions.Count.ShouldBe(0);
        }
        
        private async Task SetThresholdFee(long callingFee)
        {
            await DefaultTester.SetMethodCallingThreshold.SendAsync(new SetMethodCallingThresholdInput
            {
                Method = nameof(DefaultTester.DummyMethod),
                SymbolToAmount =
                {
                    {"ELF", callingFee}
                },
                ThresholdCheckType = ThresholdCheckType.Balance
            });

            var callingThreshold = await DefaultTester.GetMethodCallingThreshold.CallAsync(new StringValue
            {
                Value = nameof(DefaultTester.DummyMethod)
            });
            callingThreshold.SymbolToAmount["ELF"].ShouldBe(callingFee);
        }

        private async Task SetMultipleThresholdFee(long elfFeeAmount, long writeFeeAmount)
        {
            var setThresholdResult = await DefaultTester.SetMethodCallingThreshold.SendAsync(new SetMethodCallingThresholdInput
            {
                Method = nameof(DefaultTester.DummyMethod),
                SymbolToAmount =
                {
                    { "ELF", elfFeeAmount },
                    { "WRITE", writeFeeAmount }
                },
                ThresholdCheckType = ThresholdCheckType.Balance
            });
            setThresholdResult.TransactionResult.Status.ShouldBe(TransactionResultStatus.Mined);

            var callingThreshold = await DefaultTester.GetMethodCallingThreshold.CallAsync(new StringValue
            {
                Value = nameof(DefaultTester.DummyMethod)
            });
            callingThreshold.SymbolToAmount["ELF"].ShouldBe(elfFeeAmount);
            callingThreshold.SymbolToAmount["WRITE"].ShouldBe(writeFeeAmount); 
        }
    }
}