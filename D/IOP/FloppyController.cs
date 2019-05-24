﻿/*
    BSD 2-Clause License

    Copyright Vulcan Inc. 2017-2018 and Living Computer Museum + Labs 2018
    All rights reserved.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions are met:

    * Redistributions of source code must retain the above copyright notice, this
      list of conditions and the following disclaimer.

    * Redistributions in binary form must reproduce the above copyright notice,
      this list of conditions and the following disclaimer in the documentation
      and/or other materials provided with the distribution.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
    AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
    IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED.IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
    FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
    DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
    SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
    CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
    OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
    OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/


using D.IO;
using D.Logging;
using System;

namespace D.IOP
{
    /// <summary>
    /// This implements the WD FD1797 controller and the IOP's external floppy state registers.
    /// It implements the IDMAInterface so that DMA transfers can take place.
    /// 
    /// TODO: Would be useful to support recording the interleave written by Write Track commands.
    /// We always assume a 1:1 interleave, which is all the Star ever appears to use.
    /// </summary>
    public class FloppyController : IIOPDevice, IDMAInterface
    {
        public FloppyController(FloppyDrive drive, DSystem system)
        {
            _system = system;
            _drive = drive;
    }

        public FloppyDrive Drive
        {
            get { return _drive; }
        }

        public int[] ReadPorts
        {
            get { return _readPorts; }
        }

        public int[] WritePorts
        {
            get { return _writePorts; }
        }

        public bool Interrupt
        {
            get { return _interruptPending; }
        }
        
        //
        // IDMAInterface methods and properties
        //
        public bool DRQ
        {
            get
            {
                if (_drq)
                {
                    _drqCounter--;

                    if (_drqCounter == 0)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        public byte DMARead()
        {
            if (!_drq)
            {
                throw new InvalidOperationException("Unexpected DMA read with DRQ low.");
            }

            //
            // We cheat (as with reading the Data register): rather than emulating the timing of data moving
            // past the floppy drive heads, since we read the entire sector in at once
            // we assume the data's always ready so we keep DRQ high until the data has all been read.
            // This means we won't be simulating DATA LATE errors.
            //
            // Return the next byte, if any.
            // DRQ will remain raised until the DMA transfer is complete.
            //
            byte dmaRead = 0;

            if (_sectorDataIndex < _sectorBuffer.Length)
            {
                dmaRead = _sectorBuffer[_sectorDataIndex];
            }
            else
            {
                if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "DMA sector read past end of sector.");
            }

            _drqCounter = 16;
            _sectorDataIndex++;

            return dmaRead;
        }

        public void DMAWrite(byte value)
        {
            if (!_drq)
            {
                throw new InvalidOperationException("Unexpected DMA write with DRQ low.");
            }

            if (_lastCommand == FDCCommand.WriteSectorSingle)
            {
                //
                // We cheat (as with DMA reads): rather than emulating the timing of data moving
                // past the floppy drive heads, we assume the data's always ready so we keep DRQ 
                // high until the data has all been written.
                // This means we won't be simulating DATA LATE errors.
                //
                // Write next byte, and finish the transfer if this is the last.
                // DRQ will remain raised until the DMA transfer is complete.
                //
                if (_sectorDataIndex < _sectorBuffer.Length)
                {
                    _sectorBuffer[_sectorDataIndex] = value;
                }
                else
                {
                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "DMA sector write past end of sector.");
                }

                _sectorDataIndex++;
                _drqCounter = 16;
                
            }
            else if (_lastCommand == FDCCommand.WriteTrack)
            {
                //
                // This is unexpected:  polled write should be used for Write Track commands
                // to allow for very specific timing criteria to be met.
                //
                throw new InvalidOperationException("Unexpected Write Track w/DMA transfer.");
            }
            else
            {
                throw new InvalidOperationException(
                    String.Format("DMA write with unexpected command {0}", _lastCommand));
            }
        }
        
        public void DMAComplete()
        {
            if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "DMA transfer completed.");
            FinishDataTransfer();
        }

        public void WritePort(int port, byte value)
        {
            if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "FDC port {0} write {1:x2}", (FDCPorts)port, value);

            switch ((FDCPorts)port)
            {
                case FDCPorts.ExtFDCState:
                    _extState = (FDCStateFlags)value;
                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "FDC Ext state {0} ({1:x2})", (FDCStateFlags)value, value);

                    _drive.DriveSelect = (_extState & FDCStateFlags.DriveSelect) != 0;
                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Drive selected: {0}", _drive.DriveSelect);

                    _doubleDensity = (_extState & FDCStateFlags.Density) != 0;
                    _sideSelect = (_extState & FDCStateFlags.Side) != 0;

                    // Enable or disable the FDC chip
                    if ((_extState & FDCStateFlags.EnableFDC) != 0)
                    {
                        EnableFDC();
                    }
                    else
                    {
                        DisableFDC();
                    }
                    break;

                case FDCPorts.FDCTrack:
                    _fdcTrack = value;
                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "FDC track {0}", value);
                    break;

                case FDCPorts.FDCSector:
                    _fdcSector = value;
                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "FDC sector {0}", value);
                    break;

                case FDCPorts.FDCData:
                    _fdcData = value;
                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "FDC data 0x{0:x}", value);

                    if (_drq)
                    {
                        if (_lastCommand == FDCCommand.WriteTrack)
                        {
                            // There's a write track pending, capture the data written by the 8085.
                            // If we've hit the next Index pulse, we are done.
                            if (_drive.Index)
                            {
                                FinishWriteTrack();
                                if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Polled write track completed.");
                            }
                            else
                            {
                                if (_writeTrackDataIndex < _writeTrackBuffer.Length)
                                {
                                    _writeTrackBuffer[_writeTrackDataIndex] = _fdcData;
                                    _writeTrackDataIndex++;
                                }
                                else
                                {
                                    // Just log the overrun.
                                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Polled write track overflowed track buffer.");
                                }
                            }
                        }
                        else if (_lastCommand == FDCCommand.WriteSectorSingle)
                        {
                            // Just a normal write.
                            if (_sectorDataIndex < _sectorBuffer.Length - 1)
                            {
                                _sectorBuffer[_sectorDataIndex] = _fdcData;
                                _sectorDataIndex++;

                                if (_sectorDataIndex > _sectorBuffer.Length - 1)
                                {
                                    FinishDataTransfer();

                                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Polled sector write completed.");
                                }
                            }
                        }
                    }
                    break;

                case FDCPorts.FDCCommand:
                    DoCommand(value);                    
                    break;

                default:
                    throw new InvalidOperationException(String.Format("Unexpected write to port {0:x2}", port));
            }
        }

        public byte ReadPort(int port)
        {
            byte value = 0;
            
            switch ((FDCPorts)port)
            {
                case FDCPorts.FDCTrack:
                    value = _fdcTrack;
                    break;

                case FDCPorts.FDCSector:
                    value = _fdcSector;
                    break;

                case FDCPorts.FDCData:
                    if (_drq)
                    {
                        // There's a read pending, read the next byte in if any.
                        if (_sectorDataIndex < _sectorBuffer.Length - 1)
                        {
                            _fdcData = _sectorBuffer[_sectorDataIndex];
                            _sectorDataIndex++;

                            if (_sectorDataIndex > _sectorBuffer.Length - 1)
                            {
                                FinishDataTransfer();

                                if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Polled sector read completed.");
                            }
                        }
                    }

                    value = _fdcData;
                    break;

                case FDCPorts.FDCStatus:
                    value = ReadStatus();
                    break;

                case FDCPorts.ExtFDCStatusReg:
                    value = ReadExtStatus();
                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Floppy ext status read {0}", (FDCStatusFlags)value);
                    break;

                default:
                    throw new InvalidOperationException(String.Format("Unexpected read from port {0:x2}", port));
            }

            return value;

        }

        public void Reset()
        {
            _extState = FDCStateFlags.None;
            _lastCommand = FDCCommand.Restore;
            _fdcEnabled = false;
            _sectorDataIndex = 0;
            _drqCounter = 16;
            _indexReset = false;
            _doubleDensity = false;
            _sideSelect = false;

            _drive.Reset();

            ResetFlags();
        }

        private void ResetFlags()
        {
            _fdcData = 0;
            _fdcSector = 0;
            _fdcTrack = 0;
            _commandAbort = false;
            
            _crcError = false;
            _busy = false;
            _headLoaded = false;
            _seekError = false;
            _recordTypeWriteFault = false;
            _rnf = false;
            _lostData = false;
            _drq = false;

            ClearInterrupt();
        }

        //
        // Returns the status given the last command executed.
        //
        private byte ReadStatus()
        {
            // Interrupt signal is reset when the status register is read.
            ClearInterrupt();            

            byte value = 0;

            switch(_lastCommand)
            {
                // Type I Commands
                case FDCCommand.Restore:
                case FDCCommand.Seek:
                case FDCCommand.StepNoUpdate:
                case FDCCommand.StepUpdate:
                case FDCCommand.StepInNoUpdate:
                case FDCCommand.StepInUpdate:
                case FDCCommand.StepOutNoUpdate:
                case FDCCommand.StepOutUpdate:
                    value =
                        (byte)((NotReady() ? 0x80 : 0x00) |
                               (WriteProtect() ? 0x40 : 0x00) |
                               (_headLoaded ? 0x20 : 0x00) |
                               (_seekError ? 0x10 : 0x00) |
                               (_crcError ? 0x08 : 0x00) |
                               (Track0() ? 0x04 : 0x00) |
                               (Index() ? 0x02 : 0x00) |
                               (_busy ? 0x01 : 0x00));
                    break;

                // Type II/III Commands
                case FDCCommand.ReadAddress:
                    value =
                        (byte)((NotReady() ? 0x80 : 0x00) |
                               (_rnf ? 0x10 : 0x00) |
                               (_crcError ? 0x08 : 0x00) |
                               (_lostData ? 0x04 : 0x00) |
                               (_drq ? 0x02 : 0x00) |
                               (_busy ? 0x01 : 0x00));
                    break;

                case FDCCommand.ReadTrack:
                    value =
                        (byte)((NotReady() ? 0x80 : 0x00) |
                               (_lostData ? 0x04 : 0x00) |
                               (_drq ? 0x02 : 0x00) |
                               (_busy ? 0x01 : 0x00));
                    break;

                case FDCCommand.ReadSectorMultiple:
                case FDCCommand.ReadSectorSingle:
                    value =
                        (byte)((NotReady() ? 0x80 : 0x00) |
                               (_recordTypeWriteFault ? 0x20 : 0x00) |
                               (_rnf ? 0x10 : 0x00) |
                               (_crcError ? 0x08 : 0x00) |
                               (_lostData ? 0x04 : 0x00) |
                               (_drq ? 0x02 : 0x00) |
                               (_busy ? 0x01 : 0x00));
                    break;

                case FDCCommand.WriteSectorMultiple:
                case FDCCommand.WriteSectorSingle:
                    value =
                        (byte)((NotReady() ? 0x80 : 0x00) |
                               (WriteProtect() ? 0x40 : 0x00) |
                               (_recordTypeWriteFault ? 0x20 : 0x00) |
                               (_rnf ? 0x10 : 0x00) |
                               (_crcError ? 0x08 : 0x00) |
                               (_lostData ? 0x04 : 0x00) |
                               (_drq ? 0x02 : 0x00) |
                               (_busy ? 0x01 : 0x00));
                    break;

                case FDCCommand.WriteTrack:
                    value =
                        (byte)((NotReady() ? 0x80 : 0x00) |
                               (WriteProtect() ? 0x40 : 0x00) |
                               (_recordTypeWriteFault ? 0x20 : 0x00) |
                               (_lostData ? 0x04 : 0x00) |
                               (_drq ? 0x02 : 0x00) |
                               (_busy ? 0x01 : 0x00));
                    break;
            }

            if (Log.Enabled) Log.Write(
                LogComponent.IOPFloppy, 
                "FDC status read {0:x} ({1}) (last command {2})", 
                value, 
                (Type1Status)value, 
                _lastCommand);

            return value;
        }

        /// <summary>
        /// Reads the IOP's external floppy status register
        /// </summary>
        private byte ReadExtStatus()
        {
            bool doubleSided = false;
            bool sa800 = false;
            bool diskChange = false;

            if (_drive.DriveSelect)
            {
                doubleSided = !_drive.IsSingleSided;
                sa800 = !_drive.IsLoaded;
                diskChange = _drive.DiskChange;
            }            

            byte extStatus = (byte)(
                (diskChange ? 0x80 : 0x00) |     
                (_system.IOP.DMAController.TC ? 0x40 : 0x00) |      // end count
                (doubleSided ? 0x20 : 0x00) |
                (sa800 ? 0x10 : 0x00));

            if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Ext Status 0x{0:x2}", extStatus);

            return extStatus;
        }

        private void EnableFDC()
        {
            if (_fdcEnabled)
            {
                // Already enabled, no need to do anything.
                return;
            }

            //
            // After enabling the FDC, the FDC comes out of reset.
            // Technically this takes about 12ms, we're just 
            // doing it immediately.
            //
            _fdcEnabled = true;
            if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "FDC enabled.");

            // "A logic low on the [-MR] input resets the device and loads HEX 03
            //  into the command register.  The Not Ready (status bit 7) is reset
            //  during -MR ACTIVE.  When -MR is brought to a logic high, a RESTORE
            //  command is executed, regardless of the state of the Ready signal
            //  from the drive.  Also, HEX 01 is loaded into the sector register."

            // Send the RESTORE command.
            DoCommand((byte)FDCCommand.Restore << 4);

            //
            // If DriveSelect is high, we will set INDEX to high and schedule an event
            // to reset it after a short duration.  This makes FDCTest happy, and
            // is undocumented in the WD spec.
            //
            if (_drive.DriveSelect)
            {
                _indexReset = true;

                _system.Scheduler.Schedule(_resetIndexDuration,
                    (timestampNsec, context) =>
                    {
                        // Reset the index signal now.
                        _indexReset = false;

                        if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Resetting INDEX signal after FDC reset.");
                    });

                if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Setting INDEX signal after FDC reset.");
            }
        }

        private void DisableFDC()
        {
            if (!_fdcEnabled)
            {
                // Already disabled, no need to do anything.
                return;
            }

            _fdcEnabled = false;
            if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "FDC disabled.  Resetting FDC.");

            //
            // "A logic low on the [-MR] input resets the device and loads HEX 03
            //  into the command register.  The Not Ready (status bit 7) is reset
            //  during -MR ACTIVE."
            //
            // TODO:
            // HEX 03 only affects the step rate, which I'm not particularly concerned
            // about at the moment.
            //
            ResetFlags();
            _extState = FDCStateFlags.None;

            _lastCommand = FDCCommand.Restore;
        }

        private void DoCommand(int commandData)
        {
            // Interrupt signal is reset when the command register is written.
            ClearInterrupt();

            _commandAbort = false;
            FDCCommand command = (FDCCommand)(commandData >> 4);

            // Save the last command so we know what status register set to access;
            // ForceInterrupt is the exception to this rule.
            if (command != FDCCommand.ForceInterrupt)
            {
                if (_busy)
                {
                    // Early abort if a command is already in progress.
                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "FDC busy, seek aborted.", _stepDirection);
                    return;
                }

                _lastCommand = command;
            }
            else
            {
                // LastCommand gets reset to 0 on a ForceInterrupt.
                _lastCommand = FDCCommand.Restore;
            }

            int data = commandData & 0x1f;            

            switch(command)
            {
                case FDCCommand.Restore:
                    // Seek inward until the Track 0 sensor trips.
                    // We use the general Seek mechanism, which we
                    // cheat by loading the FDC track register with the
                    // actual physical head position, so a seek to 0
                    // will actually end up at physical track 0.
                    _fdcTrack = (byte)_drive.Track;
                    Seek(0, new Type1CommandParams(data));
                    break;

                case FDCCommand.Seek:
                    //
                    // "This command assumes that the Track Register contains the track number
                    //  of the current position of the Read-Write head and the Data Register contains
                    //  the desired track number."
                    //
                    Seek(_fdcData, new Type1CommandParams(data));
                    break;

                case FDCCommand.StepNoUpdate:
                case FDCCommand.StepUpdate:
                    Step(StepDirection.Last, new Type1CommandParams(data));
                    break;

                case FDCCommand.StepInNoUpdate:
                case FDCCommand.StepInUpdate:
                    Step(StepDirection.In, new Type1CommandParams(data));
                    break;

                case FDCCommand.StepOutNoUpdate:
                case FDCCommand.StepOutUpdate:
                    Step(StepDirection.Out, new Type1CommandParams(data));
                    break;

                case FDCCommand.ReadSectorSingle:
                    SectorTransfer(new Type2CommandParams(data), true /* read */);
                    break;

                case FDCCommand.WriteSectorSingle:
                    SectorTransfer(new Type2CommandParams(data), false);
                    break;                

                case FDCCommand.ForceInterrupt:
                    ForceInterrupt(data);
                    break;

                case FDCCommand.WriteTrack:
                    WriteTrack();
                    break;

                default:
                    throw new NotImplementedException(
                        String.Format("FDC command {0} not implemented.", command));
            }
        }

        private void Seek(int track, Type1CommandParams p)
        {
            _seekDestination = track;
            _seekError = false;

            // Schedule the first step of the heads
            _system.Scheduler.Schedule(_commandBeginNsec, p, SeekCallback);

            if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Seek to {0} initialized.", track);
        }

        private void Step(StepDirection direction, Type1CommandParams p)
        {
            if (direction != StepDirection.Last)
            {                
                _stepDirection = direction;
            }

            _seekError = false;

            // Schedule a step of the heads.
            if (!_busy)
            {
                _system.Scheduler.Schedule(
                    _commandBeginNsec,
                    (timestampNsec, context) =>
                    {
                        if (_commandAbort)
                        {
                            //
                            // Abort the step, perform no head steps and update no registers.
                            //
                            if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Step aborted by ForceInterrupt.");
                            return;
                        }

                        if (!_busy)
                        {
                            // Raise the busy flag -- the command has been accepted.
                            _busy = true;

                            // Schedule the actual step
                            _system.Scheduler.Schedule(
                                _stepTimeNsec,
                                (ts, ctx) =>
                                {
                                    switch (_stepDirection)
                                    {
                                        case StepDirection.Out: // one track closer to the outer edge
                                            _drive.SeekTo(_drive.Track - 1);

                                            if (p.Update)
                                            {
                                                _fdcTrack--;
                                            }
                                            break;

                                        case StepDirection.In: // one track closer to the inner edge
                                            _drive.SeekTo(_drive.Track + 1);

                                            if (p.Update)
                                            {
                                                _fdcTrack++;
                                            }
                                            break;

                                        default:
                                            throw new InvalidOperationException("Unepxected step type.");
                                    }

                                    if (p.Verify && _drive.IsLoaded && (_fdcTrack != _drive.Track))
                                    {
                                        _seekError = true;
                                    }

                                    _headLoaded = p.HeadLoad;

                                    RaiseInterrupt();

                                    //
                                    // Reset the busy flag, the step is complete.
                                    //
                                    _busy = false;

                                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Step to {0} (physical {1}) completed.", _seekDestination, _drive.Track);
                                });
                        }
                    });
            }

            if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Step {0} initialized.", _stepDirection);
        }

        private void SectorTransfer(Type2CommandParams p, bool read)
        {
            // Schedule the operation.
            _system.Scheduler.Schedule(
                _commandBeginNsec,
                (timestampNsec, context) =>
            {
                Track t = _drive.Disk.GetTrack(_drive.Track, p.SideSelect ? 1 : 0);

                //
                // Set RNF if specified sector is out of range or if the drive's physical
                // head position != the FDC's track register.
                //
                _rnf = (_fdcTrack != _drive.Track) ||
                       (_fdcSector > t.SectorCount);


                //
                // Set CRC error if selected track's format isn't compatible with this FDC,
                // (in the event that some random IMD image is loaded...)
                // or if the Density flag is incompatible with the track's format.
                //
                if ((t.Format != Format.FM500 &&
                    t.Format != Format.MFM500) ||
                    ((t.Format == Format.FM500) ^ !_doubleDensity))
                {
                    _crcError = true;
                }

                bool writeProtect = !read && _drive.IsWriteProtected;

                //
                // Actually kick off the transfer.
                // BUSY remains active until the transfer is complete -- whenever DMA or a polled
                // read finishes the buffer off.
                //
                if (!NotReady() && !_rnf && !_crcError && !writeProtect)
                {
                    // the FDC's sector value is 1-indexed.
                    _sectorBuffer = _drive.Disk.GetSector(_drive.Track, p.SideSelect ? 1 : 0, _fdcSector - 1).Data;
                    _sectorDataIndex = 0;
                    _busy = true;
                    _drq = true;
                    _drqCounter = 16;

                    if (!read)
                    {
                        _drive.Disk.SetModified();
                    }

                    if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Sector {0} of C/H/S {1}/{2}/{3} initialized.",
                        read ? "read" : "write", _fdcTrack, p.SideSelect ? 1 : 0, _fdcSector);
                }
                else
                {
                    _sectorBuffer = null;
                    _busy = false;
                }
            });

            if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Transfer initialized.");
        }

        private void WriteTrack()
        {
            // Schedule the track write.
            _system.Scheduler.Schedule(
                _commandBeginNsec,
                (timestampNsec, context) =>
                {
                    //
                    // Create a buffer for the FDC track data.
                    // BUSY remains active until the next INDEX on the drive.
                    // write finishes the buffer off.
                    //
                    if (!NotReady() && !_rnf && !_drive.IsWriteProtected)
                    {
                        _writeTrackBuffer = new byte[0x10000];  // this is about 4x more than we need
                        _writeTrackDataIndex = 0;
                        _writeTrackSide = _sideSelect;
                        _busy = true;
                        _drq = true;
                        _drqCounter = 16;

                        _drive.Disk.SetModified();
                        
                        if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, 
                            "Track write of C/H {0}/{1} initialized.",
                            _fdcTrack, _writeTrackSide ? 1 : 0); 

                    }
                    else
                    {
                        _writeTrackBuffer = null;
                        _busy = false;
                    }
                });

            if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Write track initialized.");
        }

        private void ForceInterrupt(int flags)
        {
            //
            // Terminate any pending command.
            // and generate an interrupt for the condition specified in
            // i0-i3.
            // This is only used in FDCTest.asm and doesn't actually use
            // the interrupt facilities

            // Clear the BUSY flag
            _busy = false;

            // Cause any pending command to abort before completing.
            _commandAbort = true;

            if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Force Interrupt 0x{0:x2}", flags);
        }

        private void SeekCallback(ulong skewNsec, object context)
        {
            if (_commandAbort)
            {
                //
                // Abort the seek, perform no head steps and update no registers.
                //
                if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Seek aborted by ForceInterrupt.");
                return;
            }

            // Set the FDC busy status.
            _busy = true;

            if (_fdcTrack == _seekDestination)
            {
                // We've arrived.  Everyone out of the car.
                // Clear the FDC Type I BUSY status
                _busy = false;
                
                //
                // Do a verification.  Since we're not emulating the floppy at a level
                // low enough to verify header CRC and sector IDs, we assume those are OK
                // and only compare our Physical track counter with the FDC's track counter.
                //
                Type1CommandParams p = (Type1CommandParams)context;
                if (p.Verify && _drive.IsLoaded && (_fdcTrack != _drive.Track))
                {
                    _seekError = true;
                }

                _headLoaded = p.HeadLoad;

                RaiseInterrupt();

                if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Seek to {0} (physical {1}) completed.", _seekDestination, _drive.Track);
            }            
            else
            {
                // Not there yet, move one step in the right direction.
                if (_fdcTrack < _seekDestination)
                {
                    _fdcTrack++;
                    _drive.SeekTo(_drive.Track + 1);
                }
                else
                {
                    _fdcTrack--;
                    _drive.SeekTo(_drive.Track - 1);
                }
                
                _system.Scheduler.Schedule(_stepTimeNsec, context, SeekCallback);

                if (Log.Enabled) Log.Write(LogComponent.IOPFloppy, "Seek step to {0} (physical {1})", _fdcTrack, _drive.Track);
               
            }
        }

        /// <summary>
        /// Invoked when a sector data transfer completes (either by DMA or programmed I/O).
        /// </summary>
        private void FinishDataTransfer()
        {
            _drq = false;
            _busy = false;
            _sectorBuffer = null;
            _sectorDataIndex = 0;

            RaiseInterrupt();
        }

        private void FinishWriteTrack()
        {
            //
            // Process the data the 8085 sent us and figure out what this track looks like.
            // Special values are used to indicate the start of the data we're interested in:
            // - 0xfc marks the soft index mark
            // - 0xfe marks the beginning of the sector ID record
            // - 0xfb marks the beginning of the data record
            // - 0xf7 marks the end of a record (used to cause the FDC to write a CRC)
            //
            // The Sector ID record is four bytes long and looks like:
            // Byte 0 : Track
            // Byte 1 : Head
            // Byte 2 : Sector (1-indexed)
            // Byte 3 : Sector length (0 = 128, 1 = 256, 2 = 512, 3 = 1024)
            //
            // We parse the data and grab the sector information, checking things for sanity.
            // It is possible for the 8085 code to send the FDC data that does weird things --
            // varying sector sizes per track, discontinuous sector numbers, invalid cylinder/
            // head values.  The IMD format doesn't support some of these possibilities, and
            // we don't expect them to occur (the formatting code running on the IOP does
            // sane things.)  But we do basic sanity checking here because we're not hackers.
            //
            // For now we throw if any inconsistency is found, erring on the side of not ending
            // up with randomly corrupted floppy data.
            //
            bool[] formattedSectors = new bool[256];
            int sectorSize = 0;
            TrackParseState state = TrackParseState.Gap4;

            if (Log.Enabled) Log.Write(
                                LogComponent.IOPFloppy,
                                "Processing Write Track data, length {0}",_writeTrackDataIndex);

            for (int i = 0; i < _writeTrackDataIndex; i++)
            {
                byte b = _writeTrackBuffer[i];
                switch(state)
                {
                    case TrackParseState.Gap4:
                        //
                        // Check the value supplied by the 8085 for the Gap4 value:                       
                        // Make sure it jibes with the -DDEN signal to the FDC.
                        //
                        if ((b != (byte)WriteTrackMarkers.Gap4MFM && b != (byte)WriteTrackMarkers.Gap4FM) ||
                            ((b == (byte)WriteTrackMarkers.Gap4MFM) ^ _doubleDensity))
                        {
                            throw new InvalidOperationException(
                                String.Format("Unexpected Gap4 value 0x{0:x2} with double density {1}",
                                    b, _doubleDensity));
                        }

                        state = TrackParseState.IndexMark;
                        break;

                    case TrackParseState.IndexMark:
                        if (b == (byte)WriteTrackMarkers.SoftIndex)
                        {
                            // Found the index mark, wait for the ID mark
                            state = TrackParseState.IDRecordMark;
                        }
                        break;

                    case TrackParseState.IDRecordMark:
                        if (b == (byte)WriteTrackMarkers.SectorIDRecord)
                        {
                            // Found the ID mark, read the sector ID information:
                            byte track = _writeTrackBuffer[++i];
                            byte head = _writeTrackBuffer[++i];
                            byte sector = _writeTrackBuffer[++i];
                            byte sectorLength = _writeTrackBuffer[++i];

                            // Sanity check that the track in the header matches the
                            // current track.
                            if (track != _fdcTrack)
                            {
                                throw new InvalidOperationException("WriteTrack: track != current track");
                            }

                            // Check that the head value matches the current side select value.
                            if ((head == 0) ^ !_writeTrackSide)
                            {
                                throw new InvalidOperationException(
                                    String.Format("WriteTrack: invalid head (specified {0}, FDC SSO is {1})", 
                                        head, _writeTrackSide));
                            }

                            //
                            // Determine the appropriate sector size:
                            //
                            int currentSectorSize = 0;
                            switch (sectorLength)
                            {
                                case 0:
                                    currentSectorSize = 128;
                                    break;

                                case 1:
                                    currentSectorSize = 256;
                                    break;

                                case 2:
                                    currentSectorSize = 512;
                                    break;

                                case 3:
                                    currentSectorSize = 1024;
                                    break;

                                default:
                                    throw new InvalidOperationException("WriteTrack: unexpected sector size.");
                            }

                            if (sectorSize == 0)
                            {
                                sectorSize = currentSectorSize;
                            }
                            else if (sectorSize != currentSectorSize)
                            {
                                // We don't expect (and cannot support) sectors of varying sizes
                                // on a single track.
                                throw new InvalidOperationException("WriteTrack: multiple sector sizes per track not supported.");
                            }

                            if (!formattedSectors[sector - 1])
                            {
                                formattedSectors[sector - 1] = true;
                            }
                            else
                            {
                                throw new InvalidOperationException(
                                    String.Format("WriteTrack: Duplicate sector {0}", sector - 1));
                            }

                            // And start reading in the sector data.
                            state = TrackParseState.DataRecordMark;

                            if (Log.Enabled) Log.Write(
                                LogComponent.IOPFloppy,
                                "Processing Sector {0}", sector - 1);
                        }
                        break;

                    case TrackParseState.DataRecordMark:
                        if (b == (byte)WriteTrackMarkers.DataRecord)
                        {
                            // Start of data record, read in until we hit the CRC mark (0xf7)
                            // and validate the result.  The data itself is ignored (it's just
                            // dummy data passed to the FDC)
                            int dataLength = 0;
                            while (i < _writeTrackDataIndex && _writeTrackBuffer[++i] != (byte)WriteTrackMarkers.CRC)
                            {
                                dataLength++;
                            }

                            if (dataLength != sectorSize)
                            {
                                throw new InvalidOperationException(
                                    String.Format("WriteTrack: data record length 0x{0:x} != sector size 0x{1:x}",
                                    dataLength, sectorSize));
                            }

                            // Start the next sector.
                            state = TrackParseState.IDRecordMark;
                        }
                        break;
                }
            }

            // OK, we've parsed the data and have an array of sectors.  Let's find out how many
            // there are and ensure there are no gaps in the sector numbering.
            int sectorCount = 0;
            bool foundGap = false;
            for (int i = 0; i < formattedSectors.Length; i++)
            {
                if (!formattedSectors[i])
                {
                    foundGap = true;
                }
                else
                {
                    if (foundGap)
                    {
                        throw new InvalidOperationException("WriteTrack: discontinous sector IDs");
                    }
                    else
                    {
                        sectorCount++;
                    }
                }
            }

            //
            // Format the track with the given parameters.
            //
            _drive.Disk.FormatTrack(
                _doubleDensity ? Format.MFM500 : Format.FM500,
                _fdcTrack, 
                _writeTrackSide ? 1 : 0, 
                sectorCount, 
                sectorSize);

            if (Log.Enabled) Log.Write(
                LogComponent.IOPFloppy, 
                "Write track of C/H {0}/{1} with format {1}, sector count {2} sector size {3}", 
                _fdcTrack, 
                _writeTrackSide ? 1 : 0,
                sectorCount,
                sectorSize);

            // Complete the operation and prepare for the next.
            _drq = false;
            _busy = false;
            _writeTrackBuffer = null;
            _writeTrackDataIndex = 0;

            RaiseInterrupt();
        }

        private void ClearInterrupt()
        {
            _interruptPending = false;
            // RST7.5 is edge triggered, no need to clear it here.
        }

        private void RaiseInterrupt()
        {
            _interruptPending = true;
            _system.IOP.CPU.RaiseExternalInterrupt(InterruptType.RST7_5);
        }

        private bool NotReady()
        {
            return _drive.DriveSelect ? !_drive.IsLoaded : true;
        }

        private bool WriteProtect()
        {
            return _drive.IsLoaded && _drive.IsWriteProtected;
        }

        private bool Track0()
        {
            return _drive.Track0;
        }

        private bool Index()
        {
            return _drive.Index || _indexReset;
        }

        private readonly int[] _readPorts = new int[]
            {
                (int)FDCPorts.FDCStatus,
                (int)FDCPorts.FDCTrack,
                (int)FDCPorts.FDCSector,
                (int)FDCPorts.FDCData,
                (int)FDCPorts.ExtFDCStatusReg,
            };

        private readonly int[] _writePorts = new int[]
            {
                (int)FDCPorts.FDCCommand,
                (int)FDCPorts.FDCTrack,
                (int)FDCPorts.FDCSector,
                (int)FDCPorts.FDCData,
                (int)FDCPorts.ExtFDCState
            };


        // FDC data
        private byte _fdcTrack;
        private byte _fdcSector;
        private byte _fdcData;

        // Sector data
        private byte[] _sectorBuffer;
        private int _sectorDataIndex;

        // Track data (for WriteTrack)
        private byte[] _writeTrackBuffer;
        private int _writeTrackDataIndex;
        private bool _writeTrackSide;

        // Status flags
        // Type 1/2/3:                
        private bool _crcError;         // 0x08
        private bool _busy;             // 0x01

        // Type 1:
        private bool _headLoaded;       // 0x20
        private bool _seekError;        // 0x10

        // Type 2/3:
        private bool _recordTypeWriteFault; // 0x20
        private bool _rnf;              // 0x10
        private bool _lostData;         // 0x04
        private bool _drq;              // 0x02

        private int _drqCounter;

        // Overrides drive Index signal immediately after FDC reset.
        private bool _indexReset;

        /// <summary>
        /// Current state of the enabled signal for the FDC.
        /// </summary>
        private bool _fdcEnabled;

        // Interrupt flag.  This is set when a command completes,
        // reset when the status register is read or the command
        // register is written to.
        private bool _interruptPending;

        private FDCCommand _lastCommand;

        // External state (written with ExtFDCState port)
        private FDCStateFlags _extState;

        // Seek state
        private int _seekDestination;
        private ulong _commandBeginNsec = 12 * Conversion.UsecToNsec;
        private ulong _stepTimeNsec = 6 * Conversion.MsecToNsec;

        // Step state
        private StepDirection _stepDirection;

        // Density select
        private bool _doubleDensity;

        // Side select (external)
        private bool _sideSelect;

        //
        // This exists because the FDCTest code expects undocumented behavior from the FDC chip.
        // Specifically, after the FDC is reset, INDEX goes high as long as a drive is selected
        // (regardless of whether a disk is in the drive or not).
        // After a short (unspecified) interval it goes low again.
        // On a reset with drive selected, we will kick off an event to do what the test expects.
        //
        private ulong _resetIndexDuration = 10 * Conversion.MsecToNsec;   // Guesswork

        //
        // Command execution state
        //
        private bool _commandAbort;

        //
        // The floppy drive we're talking to
        //
        private FloppyDrive _drive;

        //
        // The system we belong to
        //
        private DSystem _system;


        private enum FDCPorts
        {
            FDCCommand = 0x84,      // Commands (write)
            FDCStatus  = 0x84,      // Status (read)
            FDCTrack   = 0x85,      // Track register (r/w)
            FDCSector  = 0x86,      // Sector register (r/w)
            FDCData    = 0x87,      // Data register (r/w)

            // These are external to the actual FDC chip
            ExtFDCStatusReg  = 0xe8,   // External status register (read)
            ExtFDCState   = 0xe8,      // External state register (write)
        }

        /// <summary>
        /// Flags for FDCState port
        /// </summary>
        [Flags]
        private enum FDCStateFlags
        {
            EnableWaits  = 0x80,          // Set wait cycles in FDCState (what does this mean?)
            Precomp      = 0x40,          // Apparently the IOP handles write precomp itself?  (FDC chip can do this...)
            Side         = 0x20,          // Number of sides?
            Density      = 0x08,          // Connected to DDEN on FDC ( 0 = double, 1 = single)
            EnableFDC    = 0x04,          // Enables the FDC chip
            DriveSelect  = 0x01,          // enables drive select ( 0 = no drive selected, 1 = drive selected)
            None         = 0x00,
        }

        /// <summary>
        /// Flags for FDCStatusReg port
        /// From BootDefs.asm
        /// </summary>
        [Flags]
        private enum FDCStatusFlags
        {
            IntMask     = 0x80,           // FDC interrupt request status
            EndCount    = 0x40,           // FDC end count
            TwoSided    = 0x20,           // FDC two-sided bit
            SA800       = 0x10,           // "now no-floppy bit" (presumably set when no floppy is inserted in the drive)            
            None        = 0x00,
        }

        private enum FDCCommand
        {
            Restore = 0,
            Seek = 1,
            StepNoUpdate = 2,
            StepUpdate = 3,
            StepInNoUpdate = 4,
            StepInUpdate = 5,
            StepOutNoUpdate = 6,
            StepOutUpdate = 7,
            ReadSectorSingle = 8,
            ReadSectorMultiple = 9,
            WriteSectorSingle = 0xa,
            WriteSectorMultiple = 0xb,
            ReadAddress = 0xc,
            ForceInterrupt = 0x0d,
            ReadTrack = 0xe,
            WriteTrack = 0xf,
        }

        private enum StepDirection
        {
            In = 0,
            Out = 1,
            Last = 2,
        }

        /// <summary>
        /// Encapsulates Type 1 command options.
        /// </summary>
        private struct Type1CommandParams
        {
            public Type1CommandParams(bool update, bool headLoad, bool verify)
            {
                Update = update;
                HeadLoad = headLoad;
                Verify = verify;
            }

            public Type1CommandParams(int p)
            {
                Update = (p & 0x10) != 0;
                HeadLoad = (p & 0x08) != 0;
                Verify = (p & 0x04) != 0;
            }

            public bool Update;
            public bool HeadLoad;
            public bool Verify;            
        }

        /// <summary>
        /// Encapsulates Type 2 and 3 command options
        /// </summary>
        private struct Type2CommandParams
        {            
            public Type2CommandParams(int p)
            {
                SectorLength = ((p & 0x8) != 0);
                Delay = ((p & 0x4) != 0);
                SideSelect = ((p & 0x2) != 0);
                DataAddressMark = ((p & 0x1) != 0);
            }

            public bool SideSelect;
            public bool Delay;
            public bool SectorLength;
            public bool DataAddressMark;
        }

        //
        // WD FD179X status register bits
        //
        [Flags]
        private enum Type1Status
        {
            NotReady = 0x80,
            WriteProtect = 0x40,
            HeadLoaded = 0x20,
            SeekError = 0x10,
            CRCError = 0x08,
            Track0 = 0x04,
            Index = 0x02,
            Busy = 0x01,
        }

        /// <summary>
        /// Actually for type 2 or 3
        /// </summary>
        [Flags]
        private enum Type2Status
        {
            NotReady = 0x80,
            WriteProtect = 0x40,
            RecordTypeWriteFault = 0x20,
            RNF = 0x10,
            CRCError = 0x08,
            LostData = 0x04,
            DRQ = 0x02,
            Busy = 0x01,
        }

        private enum TrackParseState
        {
            Gap4,
            IndexMark,
            IDRecordMark,
            DataRecordMark,
        }

        // Special values are used to indicate the start of the data we're interested in:
        // - 0xfc marks the soft index mark
        // - 0xfe marks the beginning of the sector ID record
        // - 0xfb marks the beginning of the data record
        // - 0xf7 marks the end of a record (used to cause the FDC to write a CRC)
        // - 0x4e and 0xff are used in Gap4:  0x4e indicates MFM (double density), 
        //   0xff indicates FM (single).
        private enum WriteTrackMarkers
        {
            Gap4MFM = 0x4e,
            Gap4FM = 0xff,
            SoftIndex = 0xfc,
            SectorIDRecord = 0xfe,
            DataRecord = 0xfb,
            CRC = 0xf7,
        }
    }
}
