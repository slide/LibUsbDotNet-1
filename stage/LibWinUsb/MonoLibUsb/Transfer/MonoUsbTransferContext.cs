// Copyright � 2006-2010 Travis Robinson. All rights reserved.
// 
// website: http://sourceforge.net/projects/libusbdotnet
// e-mail:  libusbdotnet@gmail.com
// 
// This program is free software; you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation; either version 2 of the License, or 
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful, but 
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License
// for more details.
// 
// You should have received a copy of the GNU General Public License along
// with this program; if not, write to the Free Software Foundation, Inc.,
// 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA. or 
// visit www.gnu.org.
// 
// 
using System;
using System.Runtime.InteropServices;
using System.Threading;
using LibUsbDotNet.Main;
using MonoLibUsb;
using MonoLibUsb.Transfer;

namespace LibUsbDotNet.LudnMonoLibUsb.Internal
{
    internal class MonoUsbTransferContext : UsbTransfer, IDisposable
    {
        private bool mOwnsTransfer;

        private MonoUsbTransferDelegate mMonoUsbTransferCallbackDelegate;

        private MonoUsbTransfer mTransfer;

        public MonoUsbTransferContext(UsbEndpointBase endpointBase)
            : base(endpointBase)
        {
            allocTransfer(endpointBase, true);
        }

        #region IDisposable Members

        public new void Dispose()
        {
            freeTransfer();
        }

        #endregion
        private void allocTransfer(UsbEndpointBase endpointBase, bool ownsTransfer)
        {
            freeTransfer();
            mTransfer =MonoUsbTransfer.Alloc(0);
            mOwnsTransfer = ownsTransfer;
            mTransfer.Type = endpointBase.Type;
            mTransfer.Endpoint = endpointBase.EpNum;


        }
        private void freeTransfer()
        {
            if (mTransfer.IsInvalid || mOwnsTransfer == false) return;
            mTransferCancelEvent.Set();
            mTransferCompleteEvent.WaitOne(200, UsbConstants.EXIT_CONTEXT);
            mTransfer.Free();
           
        }
        public override void Fill(IntPtr buffer, int offset, int count, int timeout)
        {
            base.Fill(buffer, offset, count, timeout);

            mTransfer.Timeout =  timeout;
            mTransfer.PtrDeviceHandle = EndpointBase.Handle.DangerousGetHandle();

            mMonoUsbTransferCallbackDelegate = TransferCallback;
            mTransfer.PtrCallbackFn = Marshal.GetFunctionPointerForDelegate(mMonoUsbTransferCallbackDelegate);

            mTransfer.Type = EndpointBase.Type;
            mTransfer.Endpoint = EndpointBase.EpNum;
            
            mTransfer.ActualLength = 0;
            mTransfer.Status = 0;
            mTransfer.Flags = MonoUsbTransferFlags.None;
        }


        // Clean up the globally allocated memory. 

        ~MonoUsbTransferContext() { Dispose(); }

        public override ErrorCode Submit()
        {
            if (!mTransferCompleteEvent.WaitOne(0, UsbConstants.EXIT_CONTEXT)) return ErrorCode.ResourceBusy;

            mTransfer.PtrBuffer = NextBufPtr;
            mTransfer.Length = RequestCount;

            mTransferCompleteEvent.Reset();
            mTransferCancelEvent.Reset();

            int ret = (int) mTransfer.Submit();
            if (ret != 0)
            {
                mTransferCompleteEvent.Set();
                UsbError usbErr = UsbError.Error(ErrorCode.MonoApiError, ret, "SubmitTransfer", EndpointBase);
                if (!usbErr.Handled || FailRetries >= UsbConstants.MAX_FAIL_RETRIES_ON_HANDLED_ERROR)
                    return usbErr.ErrorCode;

                IncFailRetries();
                return ErrorCode.IoEndpointGlobalCancelRedo;
            }

            return ErrorCode.Success;
        }

        public override ErrorCode Wait(out int transferredCount)
        {
            transferredCount = 0;
            int ret = 0;
            MonoUsbError monoError;

            int failTimeOut=mTimeout;
            if (mTimeout != Timeout.Infinite && mTimeout < (int.MaxValue - 1000))
                failTimeOut = mTimeout + 1000;

            int iWait = WaitHandle.WaitAny(new WaitHandle[] {mTransferCompleteEvent, mTransferCancelEvent},
                                           failTimeOut,
                                           UsbConstants.EXIT_CONTEXT);
            switch (iWait)
            {
                case 0: // TransferCompleteEvent

                    if (mTransfer.Status == MonoUsbTansferStatus.TransferCompleted)
                    {
                        transferredCount = mTransfer.ActualLength;
                        return ErrorCode.Success;
                    }

                    string s;
                    monoError = MonoUsbApi.MonoLibUsbErrorFromTransferStatus(mTransfer.Status);
                    UsbError usbErr = UsbError.Error(ErrorCode.MonoApiError, (int)monoError, "GetOverlappedResult", EndpointBase);
                    if (!usbErr.Handled || FailRetries >= UsbConstants.MAX_FAIL_RETRIES_ON_HANDLED_ERROR)
                        return MonoUsbApi.ErrorCodeFromLibUsbError((int)monoError,out s);
                    
                    IncFailRetries();
                    return ErrorCode.IoEndpointGlobalCancelRedo;
                default: // mTransferCancelEvent, WaitTimeout
                    ret = (int) mTransfer.Cancel();
                    bool bTransferComplete = mTransferCompleteEvent.WaitOne(100, UsbConstants.EXIT_CONTEXT);
                    mTransferCompleteEvent.Set();

                    if (ret != 0 || !bTransferComplete)
                    {
                        ErrorCode ec = ret == 0 ? ErrorCode.CancelIoFailed : ErrorCode.MonoApiError;
                        UsbError.Error(ec, ret, String.Format("Wait:CancelTransfer Cancel:{0} Completed:{1}",(MonoUsbError)ret,bTransferComplete), EndpointBase);
                        return ec;
                    }

                    if (iWait == WaitHandle.WaitTimeout) return ErrorCode.IoTimedOut;
                    return ErrorCode.IoCancelled;
            }
        }

        private void TransferCallback(MonoUsbTransfer pTransfer)
        {
            mTransferCompleteEvent.Set();
        }
    }
}