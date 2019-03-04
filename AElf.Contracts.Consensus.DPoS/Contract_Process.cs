using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AElf.Common;
using AElf.Contracts.Consensus.DPoS.Extensions;
using AElf.Kernel;
using Google.Protobuf.WellKnownTypes;

namespace AElf.Contracts.Consensus.DPoS
{
    // ReSharper disable InconsistentNaming
    public partial class ConsensusContract
    {
        public void InitialTerm(Term firstTerm)
        {
            Assert(firstTerm.FirstRound.RoundNumber == 1,
                "It seems that the term number of initial term is incorrect.");

            var firstRound = firstTerm.FirstRound;
            InitialBlockchain(firstRound);

            SetAliases(firstRound);

            var senderPublicKey = Context.RecoverPublicKey().ToHex();

            // Update ProducedBlocks for sender.
            if (firstRound.RealTimeMinersInformation.ContainsKey(senderPublicKey))
            {
                firstRound.RealTimeMinersInformation[senderPublicKey].ProducedBlocks += 1;
            }
            else
            {
                // The sender isn't a initial miner, need to update its history information.
                if (TryToGetMinerHistoryInformation(senderPublicKey, out var historyInformation))
                {
                    historyInformation.ProducedBlocks += 1;
                }
                else
                {
                    // Create a new history information.
                    historyInformation = new CandidateInHistory
                    {
                        PublicKey = senderPublicKey,
                        ProducedBlocks = 1,
                        CurrentAlias = senderPublicKey.Substring(0, DPoSContractConsts.AliasLimit)
                    };
                }

                AddOrUpdateMinerHistoryInformation(historyInformation);
            }

            firstTerm.FirstRound.BlockchainAge = 1;
            TryToAddRoundInformation(firstTerm.FirstRound);
        }

        public ActionResult NextTerm(Term term)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            // Count missed time slot of current round.
            CountMissedTimeSlots();

            Assert(TryToGetTermNumber(out var termNumber), "Term number not found.");
            State.DividendContract.KeepWeights(termNumber);

            // Update current term number and current round number.
            Assert(TryToUpdateTermNumber(term.TermNumber), "Failed to update term number.");
            Assert(TryToUpdateRoundNumber(term.FirstRound.RoundNumber), "Failed to update round number.");

            // Reset some fields of first two rounds of next term.
            foreach (var minerInRound in term.FirstRound.RealTimeMinersInformation.Values)
            {
                minerInRound.MissedTimeSlots = 0;
                minerInRound.ProducedBlocks = 0;
            }

            foreach (var minerInRound in term.SecondRound.RealTimeMinersInformation.Values)
            {
                minerInRound.MissedTimeSlots = 0;
                minerInRound.ProducedBlocks = 0;
            }

            var senderPublicKey = Context.RecoverPublicKey().ToHex();

            // Update produced block number of this node.
            if (term.FirstRound.RealTimeMinersInformation.ContainsKey(senderPublicKey))
            {
                term.FirstRound.RealTimeMinersInformation[senderPublicKey].ProducedBlocks += 1;
            }
            else
            {
                if (TryToGetMinerHistoryInformation(senderPublicKey, out var historyInformation))
                {
                    historyInformation.ProducedBlocks += 1;
                }
                else
                {
                    historyInformation = new CandidateInHistory
                    {
                        PublicKey = senderPublicKey,
                        ProducedBlocks = 1,
                        CurrentAlias = senderPublicKey.Substring(0, DPoSContractConsts.AliasLimit)
                    };
                }

                AddOrUpdateMinerHistoryInformation(historyInformation);
            }

            // Update miners list.
            SetMiners(term.Miners);

            // Update term number lookup. (Using term number to get first round number of related term.)
            AddTermNumberToFirstRoundNumber(term.TermNumber, term.FirstRound.RoundNumber);

            Assert(TryToGetCurrentAge(out var blockAge), "Block age not found.");
            // Update blockchain age of next two rounds.
            term.FirstRound.BlockchainAge = blockAge;
            term.SecondRound.BlockchainAge = blockAge;

            // Update rounds information of next two rounds.
            TryToAddRoundInformation(term.FirstRound);
            TryToAddRoundInformation(term.SecondRound);

            Console.WriteLine($"Term changing duration: {stopwatch.ElapsedMilliseconds} ms.");

            TryToFindLIB();

            return new ActionResult {Success = true};
        }

        /// <summary>
        /// Take a snapshot of specific term.
        /// Basically this snapshot is used for getting ranks of candidates of specific term.
        /// </summary>
        /// <param name="snapshotTermNumber"></param>
        /// <param name="lastRoundNumber"></param>
        /// <returns></returns>
        public ActionResult SnapshotForTerm(ulong snapshotTermNumber, ulong lastRoundNumber)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            if (TryToGetSnapshot(snapshotTermNumber, out _))
            {
                return new ActionResult
                {
                    Success = false,
                    ErrorMessage = $"Snapshot of term {snapshotTermNumber} already taken."
                };
            }

            if (!TryToGetRoundInformation(lastRoundNumber, out var roundInformation))
            {
                return new ActionResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to get information of round {lastRoundNumber}."
                };
            }

            // To calculate the number of mined blocks.
            var minedBlocks = roundInformation.RealTimeMinersInformation.Values.Aggregate<MinerInRound, ulong>(0,
                (current, minerInRound) => current + minerInRound.ProducedBlocks);

            // Snapshot for the number of votes of new victories.
            var candidateInTerms = new List<CandidateInTerm>();
            if (TryToGetVictories(out var victories))
            {
                foreach (var candidatePublicKey in victories.PublicKeys)
                {
                    if (TryToGetTicketsInformation(candidatePublicKey, out var candidateTickets))
                    {
                        candidateInTerms.Add(new CandidateInTerm
                        {
                            PublicKey = candidatePublicKey,
                            Votes = candidateTickets.ObtainedTickets
                        });
                    }
                    else
                    {
                        AddOrUpdateTicketsInformation(new Tickets {PublicKey = candidatePublicKey});
                        candidateInTerms.Add(new CandidateInTerm
                        {
                            PublicKey = candidatePublicKey,
                            Votes = 0
                        });
                    }
                }
            }

            Assert(TryToGetRoundNumber(out var roundNumber), "Round number not found.");
            // Set snapshot of related term.
            SetSnapshot(new TermSnapshot
            {
                TermNumber = snapshotTermNumber,
                EndRoundNumber = roundNumber,
                TotalBlocks = minedBlocks,
                CandidatesSnapshot = {candidateInTerms}
            });

            Console.WriteLine($"Snapshot of term {snapshotTermNumber} taken.");

            Console.WriteLine($"Term snapshot duration: {stopwatch.ElapsedMilliseconds} ms.");

            return new ActionResult {Success = true};
        }

        public ActionResult SnapshotForMiners(ulong previousTermNumber, ulong lastRoundNumber)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            Assert(TryToGetRoundInformation(lastRoundNumber, out var roundInformation),
                "Round information not found.");

            foreach (var candidate in roundInformation.RealTimeMinersInformation)
            {
                CandidateInHistory candidateInHistory;
                if (TryToGetMinerHistoryInformation(candidate.Key, out var historyInformation))
                {
                    var terms = new List<ulong>(historyInformation.Terms.ToList());

                    if (terms.Contains(previousTermNumber))
                    {
                        return new ActionResult
                            {Success = false, ErrorMessage = "Snapshot for miners in previous term already taken."};
                    }

                    terms.Add(previousTermNumber);

                    var continualAppointmentCount = historyInformation.ContinualAppointmentCount;
                    if (TryToGetMiners(previousTermNumber, out var minersOfLastTerm) &&
                        minersOfLastTerm.PublicKeys.Contains(candidate.Key))
                    {
                        continualAppointmentCount++;
                    }
                    else
                    {
                        continualAppointmentCount = 0;
                    }

                    candidateInHistory = new CandidateInHistory
                    {
                        PublicKey = candidate.Key,
                        MissedTimeSlots = historyInformation.MissedTimeSlots + candidate.Value.MissedTimeSlots,
                        ProducedBlocks = historyInformation.ProducedBlocks + candidate.Value.ProducedBlocks,
                        ContinualAppointmentCount = continualAppointmentCount,
                        ReappointmentCount = historyInformation.ReappointmentCount + 1,
                        CurrentAlias = historyInformation.CurrentAlias,
                        Terms = {terms}
                    };
                }
                else
                {
                    candidateInHistory = new CandidateInHistory
                    {
                        PublicKey = candidate.Key,
                        MissedTimeSlots = candidate.Value.MissedTimeSlots,
                        ProducedBlocks = candidate.Value.ProducedBlocks,
                        ContinualAppointmentCount = 0,
                        ReappointmentCount = 0,
                        Terms = {previousTermNumber}
                    };
                }

                AddOrUpdateMinerHistoryInformation(candidateInHistory);
            }

            Console.WriteLine($"Miners snapshot duration: {stopwatch.ElapsedMilliseconds} ms.");

            return new ActionResult {Success = true};
        }

        public ActionResult SendDividends(ulong dividendsTermNumber, ulong lastRoundNumber)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            Assert(TryToGetRoundInformation(lastRoundNumber, out var roundInformation),
                "Round information not found.");

            // Set dividends of related term to Dividends Contract.
            var minedBlocks = roundInformation.RealTimeMinersInformation.Values.Aggregate<MinerInRound, ulong>(0,
                (current, minerInRound) => current + minerInRound.ProducedBlocks);
            State.DividendContract.AddDividends(dividendsTermNumber, GetDividendsForVoters(minedBlocks));

            ulong totalVotes = 0;
            ulong totalReappointment = 0;
            var continualAppointmentDict = new Dictionary<string, ulong>();
            foreach (var minerInRound in roundInformation.RealTimeMinersInformation)
            {
                if (TryToGetTicketsInformation(minerInRound.Key, out var candidateTickets))
                {
                    totalVotes += candidateTickets.ObtainedTickets;
                }

                if (TryToGetMinerHistoryInformation(minerInRound.Key, out var candidateInHistory))
                {
                    totalReappointment += candidateInHistory.ContinualAppointmentCount;

                    continualAppointmentDict.Add(minerInRound.Key, candidateInHistory.ContinualAppointmentCount);
                }

                // Transfer dividends for actual miners. (The miners list based on last round of current term.)
                var amount = GetDividendsForEveryMiner(minedBlocks) +
                             (totalVotes == 0
                                 ? 0
                                 : GetDividendsForTicketsCount(minedBlocks) * candidateTickets.ObtainedTickets /
                                   totalVotes) +
                             (totalReappointment == 0
                                 ? 0
                                 : GetDividendsForReappointment(minedBlocks) *
                                   continualAppointmentDict[minerInRound.Key] /
                                   totalReappointment);
                // TODO: Can we ask the miners to claim the rewards ???
                State.DividendContract.SendDividends(
                    Address.FromPublicKey(ByteArrayHelpers.FromHexString(minerInRound.Key)), amount);
            }

            if (TryToGetBackups(roundInformation.RealTimeMinersInformation.Keys.ToList(), out var backups))
            {
                foreach (var backup in backups)
                {
                    var backupCount = (ulong) backups.Count;
                    var amount = backupCount == 0 ? 0 : GetDividendsForBackupNodes(minedBlocks) / backupCount;
                    State.DividendContract.SendDividends(Address.FromPublicKey(ByteArrayHelpers.FromHexString(backup)),
                        amount);
                }
            }

            Console.WriteLine($"Send dividends duration: {stopwatch.ElapsedMilliseconds} ms.");

            return new ActionResult {Success = true};
        }

        public void NextRound(Forwarding forwarding)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var nextRound = forwarding.NextRound;

            if (TryToGetRoundNumber(out var roundNumber))
            {
                Assert(roundNumber < nextRound.RoundNumber, "Incorrect round number for next round.");
            }

            var senderPublicKey = Context.RecoverPublicKey().ToHex();

            nextRound.ExtraBlockProducerOfPreviousRound = senderPublicKey;

            // TODO: Miners replacement during one term.
//            if (TryToGetCurrentRoundInformation(out var currentRoundInformation) &&
//                forwarding.NextRound.RealTimeMinersInformation.Keys.Count == GetProducerNumber() &&
//                TryToGetTermNumber(out var termNumber))
//            {
//                var miners = forwarding.NextRound.RealTimeMinersInformation.Keys.ToMiners();
//                miners.TermNumber = termNumber;
//                SetMiners(miners, true);
//            }

            // Update the age of this blockchain
            SetBlockAge(nextRound.BlockchainAge);

            Assert(TryToGetPreviousRoundInformation(out var currentRound));

            // Update missed time slots and produced blocks for each miner.
            foreach (var minerInRound in currentRound.RealTimeMinersInformation)
            {
                if (nextRound.RealTimeMinersInformation.ContainsKey(minerInRound.Key))
                {
                    nextRound.RealTimeMinersInformation[minerInRound.Key].MissedTimeSlots =
                        minerInRound.Value.MissedTimeSlots;
                    nextRound.RealTimeMinersInformation[minerInRound.Key].ProducedBlocks =
                        minerInRound.Value.ProducedBlocks;
                }
                else
                {
                    if (TryToGetMinerHistoryInformation(senderPublicKey, out var historyInformation))
                    {
                        historyInformation.ProducedBlocks += minerInRound.Value.ProducedBlocks;
                        historyInformation.MissedTimeSlots += minerInRound.Value.MissedTimeSlots;
                    }
                    else
                    {
                        historyInformation = new CandidateInHistory
                        {
                            PublicKey = senderPublicKey,
                            ProducedBlocks = minerInRound.Value.ProducedBlocks,
                            MissedTimeSlots = minerInRound.Value.MissedTimeSlots,
                            CurrentAlias = senderPublicKey.Substring(0, DPoSContractConsts.AliasLimit)
                        };
                    }

                    AddOrUpdateMinerHistoryInformation(historyInformation);
                }
            }

            if (nextRound.RealTimeMinersInformation.ContainsKey(senderPublicKey))
            {
                nextRound.RealTimeMinersInformation[senderPublicKey].ProducedBlocks += 1;
            }
            else
            {
                if (TryToGetMinerHistoryInformation(senderPublicKey, out var historyInformation))
                {
                    historyInformation.ProducedBlocks += 1;
                }
                else
                {
                    historyInformation = new CandidateInHistory
                    {
                        PublicKey = senderPublicKey,
                        ProducedBlocks = 1,
                        CurrentAlias = senderPublicKey.Substring(0, DPoSContractConsts.AliasLimit)
                    };
                }

                AddOrUpdateMinerHistoryInformation(historyInformation);
            }

            TryToAddRoundInformation(nextRound);
            TryToUpdateRoundNumber(nextRound.RoundNumber);

            TryToFindLIB();
        }

        public void PackageOutValue(ToPackage toPackage)
        {
            Assert(TryToGetCurrentRoundInformation(out var currentRound) &&
                   toPackage.RoundId == currentRound.RoundId, DPoSContractConsts.RoundIdNotMatched);

            Assert(TryToGetCurrentRoundInformation(out var roundInformation),
                "Round information not found.");

            var publicKey = Context.RecoverPublicKey().ToHex();

            if (roundInformation.RoundNumber != 1)
            {
                roundInformation.RealTimeMinersInformation[publicKey].Signature = toPackage.Signature;
            }

            roundInformation.RealTimeMinersInformation[publicKey].OutValue = toPackage.OutValue;

            roundInformation.RealTimeMinersInformation[publicKey].ProducedBlocks += 1;

            roundInformation.RealTimeMinersInformation[publicKey].PromisedTinyBlocks = toPackage.PromiseTinyBlocks;

            if (toPackage.PreviousInValue != Hash.Default)
            {
                roundInformation.RealTimeMinersInformation[publicKey].PreviousInValue = toPackage.PreviousInValue;
            }
            
            TryToUpdateRoundInformation(roundInformation);

            TryToFindLIB();
        }

        public void BroadcastInValue(ToBroadcast toBroadcast)
        {
            if (TryToGetCurrentRoundInformation(out var currentRound) &&
                toBroadcast.RoundId != currentRound.RoundId)
            {
                return;
            }

            Assert(TryToGetCurrentRoundInformation(out var roundInformation),
                "Round information not found.");

            roundInformation.RealTimeMinersInformation[Context.RecoverPublicKey().ToHex()].InValue = toBroadcast.InValue;

            TryToAddRoundInformation(roundInformation);
        }

        public bool IsCurrentMiner(string publicKey)
        {
            if (!TryToGetCurrentRoundInformation(out var currentRound))
                return false;

            if (currentRound.RealTimeMinersInformation.Values.All(m => m.OutValue == null))
            {
                return currentRound.ExtraBlockProducerOfPreviousRound != null && currentRound.ExtraBlockProducerOfPreviousRound == publicKey;
            }

            return currentRound.RealTimeMinersInformation.Values.OrderByDescending(m => m.Order)
                       .First(m => m.OutValue != null).PublicKey == publicKey;
        }

        public void TryToFindLIB()
        {
            if (CalculateLIB(out var offset))
            {
                Context.LogDebug(() => $"LIB found, offset is {offset}");
                Context.FireEvent(new LIBFound
                {
                    Offset = offset
                });
            }
        }

        private bool CalculateLIB(out ulong offset)
        {
            offset = 0;

            if (TryToGetCurrentRoundInformation(out var currentRound))
            {
                var currentRoundMiners = currentRound.RealTimeMinersInformation;

                var minersCount = currentRoundMiners.Count;

                var minimumCount = ((int) ((minersCount * 2d) / 3)) + 1;
                var validMinersOfCurrentRound = currentRoundMiners.Values.Where(m => m.OutValue != null).ToList();
                var validMinersCountOfCurrentRound = validMinersOfCurrentRound.Count;

                var senderPublicKey = Context.RecoverPublicKey().ToHex();
                var senderOrder = currentRoundMiners[senderPublicKey].Order;
                if (validMinersCountOfCurrentRound > minimumCount)
                {
                    offset = (ulong) senderOrder;
                    return true;
                }

                // Current round is not enough to find LIB.

                var publicKeys = new HashSet<string>(validMinersOfCurrentRound.Select(m => m.PublicKey));

                if (TryToGetPreviousRoundInformation(out var previousRound))
                {
                    var preRoundMiners = previousRound.RealTimeMinersInformation.Values.OrderByDescending(m => m.Order)
                        .Select(m => m.PublicKey).ToList();

                    var traversalBlocksCount = publicKeys.Count;

                    for (var i = 0; i < minersCount; i++)
                    {
                        if (++traversalBlocksCount > minersCount)
                        {
                            return false;
                        }

                        var miner = preRoundMiners[i];

                        if (previousRound.RealTimeMinersInformation[miner].OutValue != null)
                        {
                            if (!publicKeys.Contains(miner))
                                publicKeys.Add(miner);
                        }

                        if (publicKeys.Count >= minimumCount)
                        {
                            offset = (ulong) validMinersCountOfCurrentRound + (ulong) i;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        #region Vital Steps

        private void InitialBlockchain(Round firstRound)
        {
            SetTermNumber(1);
            SetRoundNumber(1);
            SetBlockAge(1);
            AddTermNumberToFirstRoundNumber(1, 1);
            SetBlockchainStartTimestamp(firstRound.GetStartTime());
            var miners = firstRound.RealTimeMinersInformation.Keys.ToList().ToMiners();
            miners.TermNumber = 1;
            SetMiners(miners);
            SetMiningInterval(firstRound.GetMiningInterval());
        }

        private void SetAliases(Round round)
        {
            var index = 0;
            var aliases = DPoSContractConsts.InitialMinersAliases.Split(',');
            foreach (var publicKey in round.RealTimeMinersInformation.Keys)
            {
                if (index >= aliases.Length)
                    return;

                var alias = aliases[index];
                SetAlias(publicKey, alias);
                AddOrUpdateMinerHistoryInformation(new CandidateInHistory
                    {PublicKey = publicKey, CurrentAlias = alias});
                index++;
            }
        }

        #endregion

        public IEnumerable<string> GetVictories()
        {
            if (TryToGetVictories(out var victories))
            {
                return victories.PublicKeys;
            }

            return null;
        }

        /// <summary>
        /// Normally this process contained in NextRound method.
        /// </summary>
        private void CountMissedTimeSlots()
        {
            if (TryToGetCurrentRoundInformation(out var currentRound))
            {
                foreach (var minerInRound in currentRound.RealTimeMinersInformation)
                {
                    if (minerInRound.Value.OutValue == null)
                    {
                        minerInRound.Value.MissedTimeSlots += 1;
                    }
                }

                TryToUpdateRoundInformation(currentRound);
            }
        }
    }
}