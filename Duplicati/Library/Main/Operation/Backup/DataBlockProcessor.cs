﻿//  Copyright (C) 2015, The Duplicati Team
//  http://www.duplicati.com, info@duplicati.com
//
//  This library is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 2.1 of the
//  License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful, but
//  WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
using System;
using CoCoL;
using System.Threading.Tasks;
using Duplicati.Library.Main.Database;
using Duplicati.Library.Main.Volumes;
using Duplicati.Library.Main.Operation.Common;
using System.IO;
using System.Linq;

namespace Duplicati.Library.Main.Operation.Backup
{
    /// <summary>
    /// This class receives data blocks and compresses them
    /// </summary>
    internal static class DataBlockProcessor
    {
        public static async Task Run(BackupDatabase database, Options options)
        {
            return AutomationExtensions.RunTask(
                new
                {
                    LogChannel = ChannelMarker.ForWrite<LogMessage>("LogChannel"),
                    Input = ChannelMarker.ForRead<DataBlock>("OutputBlocks"),
                    Output = ChannelMarker.ForWrite<IBackendOperation>("BackendRequests"),
                    SpillPickup = ChannelMarker.ForWrite<IBackendOperation>("SpillPickup"),
                },

                async self =>
                {
                    BlockVolumeWriter blockvolume = null;
                    IndexVolumeWriter indexvolume = null;

                    try
                    {
                        while(true)
                        {
                            var b = await self.Input.ReadAsync();

                            if (await database.AddBlockAsync(b.HashKey, b.Size, blockvolume.VolumeID))
                            {
                                // Lazy-start a new block volume
                                if (blockvolume == null)
                                {
                                    blockvolume = new BlockVolumeWriter(options);
                                    blockvolume.VolumeID = await database.RegisterRemoteVolumeAsync(blockvolume.RemoteFilename, RemoteVolumeType.Blocks, RemoteVolumeState.Temporary);

                                    if (options.IndexfilePolicy != Options.IndexFileStrategy.None)
                                    {
                                        indexvolume = new IndexVolumeWriter(options);
                                        indexvolume.VolumeID = database.RegisterRemoteVolumeAsync(indexvolume.RemoteFilename, RemoteVolumeType.Index, RemoteVolumeState.Temporary);
                                    }
                                }

                                blockvolume.AddBlock(b.HashKey, b.Data, b.Offset, b.Size, b.Hint);

                                //TODO: In theory a normal data block and blocklist block could be equal.
                                // this would cause the index file to not contain all data,
                                // if the data file is added before the blocklist data
                                // ... highly theoretical and only causes extra block data downloads ...
                                if (options.IndexfilePolicy == Options.IndexFileStrategy.Full && b.IsBlocklistHashes)
                                    indexvolume.WriteBlocklist(b.HashKey, b.Data, b.Offset, b.Size);

                                if (blockvolume.Filesize > options.VolumeSize - options.Blocksize)
                                {
                                    if (options.Dryrun)
                                    {
                                        blockvolume.Close();
                                            await self.LogChannel.WriteAsync(LogMessage.DryRun("Would upload block volume: {0}, size: {1}", blockvolume.RemoteFilename, Library.Utility.Utility.FormatSizeString(new FileInfo(blockvolume.LocalFilename).Length)));

                                        if (indexvolume != null)
                                        {
                                            UpdateIndexVolume(indexvolume, blockvolume, database);
                                            indexvolume.FinishVolume(Library.Utility.Utility.CalculateHash(blockvolume.LocalFilename), new FileInfo(blockvolume.LocalFilename).Length);
                                            await self.LogChannel.WriteAsync(LogMessage.DryRun("Would upload index volume: {0}, size: {1}", indexvolume.RemoteFilename, Library.Utility.Utility.FormatSizeString(new FileInfo(indexvolume.LocalFilename).Length)));
                                            indexvolume.Dispose();
                                            indexvolume = null;
                                        }

                                        blockvolume.Dispose();
                                        blockvolume = null;
                                        indexvolume.Dispose();
                                        indexvolume = null;
                                    }
                                    else
                                    {
                                        //When uploading a new volume, we register the volumes and then flush the transaction
                                        // this ensures that the local database and remote storage are as closely related as possible
                                        await database.UpdateRemoteVolume(blockvolume.RemoteFilename, RemoteVolumeState.Uploading, -1, null);
                                    
                                        blockvolume.Close();
                                        UpdateIndexVolume(indexvolume, blockvolume, database);

                                        m_backend.FlushDbMessages(database, database.CurrentTransaction);
                                        m_backendLogFlushTimer = DateTime.Now.Add(FLUSH_TIMESPAN);

                                        await database.CommitTransactionAsync("CommitAddBlockToOutputFlush");

                                        await self.Output.WriteAsync(new UploadRequest(blockvolume, indexvolume));
                                        blockvolume = null;
                                        indexvolume = null;
                                    }
                                }

                            }                    
                        }
                    }
                    catch(Exception ex)
                    {
                        if (ex.IsRetiredException())
                        {
                            // If we have collected data, merge all pending volumes into a single volume
                            if (blockvolume != null)
                                await self.SpillPickup.WriteAsync(new UploadRequest(blockvolume, indexvolume));
                        }

                        throw;
                    }
                }
            );
        }

        private static void UpdateIndexVolume(IndexVolumeWriter indexvolume, BlockVolumeWriter blockvolume, LocalBackupDatabase database)
        {
            if (indexvolume != null)
            {
                lock(database.AccessLock)
                {
                    database.AddIndexBlockLink(indexvolume.VolumeID, blockvolume.VolumeID, database.CurrentTransaction);
                    indexvolume.StartVolume(blockvolume.RemoteFilename);

                    foreach(var b in database.GetBlocks(blockvolume.VolumeID))
                        indexvolume.AddBlock(b.Hash, b.Size);

                    database.UpdateRemoteVolume(indexvolume.RemoteFilename, RemoteVolumeState.Uploading, -1, null, database.CurrentTransaction);
                }
            }
        }

    }
}

