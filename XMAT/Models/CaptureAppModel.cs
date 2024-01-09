// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.Models
{
    public enum ExportType : int
    {
        Fiddler = 0
    }

    public enum ImportType : int
    {
        Fiddler = 0
    }

    internal class CaptureAppModel : ICaptureAppModel, INotifyPropertyChanged
    {
        public IDatabase ActiveDatabase { get; internal set; }

        // TODO: someday support multiple readonly databases??
        public IReadonlyDatabase LoadedDatabase { get; internal set; }

        public ObservableCollection<ICaptureDeviceContext> CaptureDeviceContexts { get => _captureDeviceContexts; }
        public ICaptureDeviceContext SelectedDeviceContext { get => _selectedDeviceContext; set { _selectedDeviceContext = value; RaisePropertyChange(); } }
        public ObservableCollection<ICaptureAnalysisRun> AnalysisRuns { get => _analysisRuns; }

        public ICaptureAnalysisRun AnalysisRunById(int id)
        {
            return AnalysisRuns.FirstOrDefault(run => id == run.Id);
        }

        private string _statusBarText1;
        private string _statusBarText2;
        private string _statusBarText3;

        public string StatusBarText1 { get => _statusBarText1; set { _statusBarText1 = value; RaisePropertyChange("StatusBarText1"); } }
        public string StatusBarText2 { get => _statusBarText2; set { _statusBarText2 = value; RaisePropertyChange("StatusBarText2"); } }
        public string StatusBarText3 { get => _statusBarText3; set { _statusBarText3 = value; RaisePropertyChange("StatusBarText3"); } }

        internal static readonly CaptureAppModel AppModel = new();

        internal IDataTable CaptureDevicesTable { get; set; }
        internal IList<ICaptureAnalyzer> CaptureAnalyzers { get => _captureAnalyzers; }
        internal AppPreferencesModel PreferencesModel { get; set; }

        private readonly ObservableCollection<ICaptureDeviceContext> _captureDeviceContexts = new();
        private ICaptureDeviceContext _selectedDeviceContext = null;
        private readonly List<ICaptureAnalyzer> _captureAnalyzers = new();
        private readonly ObservableCollection<ICaptureAnalysisRun> _analysisRuns = new();

        public CaptureAppModel()
        {
            PreferencesModel = new();
        }

        public ICaptureDeviceContext SelectCaptureDeviceContext(string deviceContextName)
        {
            ICaptureDeviceContext context = _captureDeviceContexts.FirstOrDefault(
                captureDeviceContext => captureDeviceContext.DeviceName == deviceContextName);
            if (context != null && context != SelectedDeviceContext)
            {
                SelectedDeviceContext = context;
            }
            return context;
        }

        internal void InitializeDatabase()
        {
            ActiveDatabase = DataAbstractionLayer.DataAbstractionLayer.Database;
            AppModel.ActiveDatabase.Initialize();

            // add the devices table
            AppModel.CaptureDevicesTable = AppModel.ActiveDatabase.CreateTable(
                @"CaptureDevices",
                new Field<Int64>(@"DeviceType"),
                new Field<string>(@"DeviceName"),
                new Field<Int64>(@"CaptureType")
            );
        }

        internal void AddCaptureAnalyzer(ICaptureAnalyzer analyzer)
        {
            try
            {
                analyzer.Initialize(this);
                _captureAnalyzers.Add(analyzer);
            }
            catch (Exception ex)
            {
                // since the analyzer failed to initialize, then we do not
                // add it to our active set...
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }

        internal void LoadDataCaptures(string fromFilePath)
        {
            LoadedDatabase = DataAbstractionLayer.DataAbstractionLayer.LoadDatabaseFromFile(fromFilePath);

            IDataset devicesDataset = AppModel.LoadedDatabase.TableByName(@"CaptureDevices").Dataset;

            List<IDataset> Datasets = new();

            foreach (var record in devicesDataset.DataRecords)
            {
                var device = AppModel.AddDeviceContext(
                    (DeviceType)record.Int(@"DeviceType"), 
                    record.Str(@"DeviceName"),
                    (CaptureType)record.Int(@"CaptureType"),
                    true,
                    // NOTE: since this is a READONLY
                    // capture, we do not need parameters maybe?
                    null
                );

                if (device == null)
                {
                    continue;
                }

                Datasets.Clear();
                foreach (string tableName in AppModel.LoadedDatabase.TableNames())
                {
                    if (device.CaptureController.CaptureMethod.OwnsDataTable(tableName))
                    {
                        IReadonlyDataTable table = AppModel.LoadedDatabase.TableByName(tableName);
                        Datasets.Add(table.Dataset);
                    }
                }
                device.CaptureController.LoadCaptures(Datasets);
            }
        }

        internal void SaveDataCaptures(string toFilePath)
        {
            ActiveDatabase.SaveToFile(toFilePath);
        }

        internal async Task ImportDataCapturesAsync(ImportType importType, string fromFilePath)
        {
            ICaptureDeviceContext captureDevice = null;

            switch (importType)
            {
                // create a new tab with an "Unknown" device and WebServiceCaptureControl capture method
                // that will be read-only and imports this data...
                case ImportType.Fiddler:
                    {
                        captureDevice = AppModel.AddDeviceContext(
                            DeviceType.Unknown,
                            $"{importType} - {Path.GetFileName(fromFilePath)}",
                            CaptureType.WebProxy,
                            true,
                            // NOTE: since this is a READONLY
                            // capture, we do not need parameters maybe?
                            null
                        );
                        await (captureDevice.CaptureController.CaptureMethod as WebServiceCapture.WebServiceCaptureMethod)?.ImportFromFiddlerAsync(fromFilePath, captureDevice);
                    }
                    break;
            }

            if (captureDevice != null)
            {
                AppModel.SelectedDeviceContext = captureDevice;
            }
        }

        internal void ExportDataCaptures(ExportType exportType, string toFilePath)
        {
            switch(exportType)
            {
                case ExportType.Fiddler:
                    (SelectedDeviceContext.CaptureController.CaptureMethod as WebServiceCapture.WebServiceCaptureMethod)?.ExportToFiddler(toFilePath);
                    break;
            }
        }

        internal ICaptureDeviceContext AddDeviceContext(
            DeviceType deviceType,
            string deviceName,
            CaptureType captureType,
            bool readOnly,
            object serializedParameters)
        {
            var newCaptureDeviceModel = GetDeviceContext(
                deviceType,
                deviceName,
                captureType,
                readOnly);

            if (newCaptureDeviceModel == null)
            {
                newCaptureDeviceModel = new CaptureDeviceContextModel(
                    deviceType,
                    deviceName,
                    captureType,
                    readOnly,
                    serializedParameters);
                _captureDeviceContexts.Add(newCaptureDeviceModel);

                if(!readOnly)
                {
                    AppModel.CaptureDevicesTable.AddRow(
                        (Int64)deviceType,
                        deviceName,
                        (Int64)captureType);
                }
            }

            newCaptureDeviceModel.CaptureController.ClearAllCaptures();
            return newCaptureDeviceModel;
        }

        internal ICaptureDeviceContext GetDeviceContext(DeviceType deviceType, string deviceName, CaptureType captureType, bool readOnly)
        {
            return (from cc in _captureDeviceContexts
                    where 
                    cc.DeviceType == deviceType &&
                    cc.DeviceName == deviceName &&
                    cc.CaptureType == captureType &&
                    cc.IsReadOnly == readOnly
                    select cc
            ).FirstOrDefault();
        }

        internal void RemoveDeviceContext(ICaptureDeviceContext captureDeviceContext)
        {
            // remove device from the devices table
            AppModel.CaptureDevicesTable.RemoveRowsWhere(
                new FieldValue<Int64>(@"DeviceType", (Int64)captureDeviceContext.DeviceType),
                new FieldValue<string>(@"DeviceName", captureDeviceContext.DeviceName)
            );

            captureDeviceContext.CaptureController.Close();
            _captureDeviceContexts.Remove(captureDeviceContext);
        }

        internal AnalysisRunModel AddAnalysisRun(ICaptureAnalyzer analyzer)
        {
            var headerText = $"{analyzer.Description} [{DateTime.Now}]";
            var id = (Int64)DateTime.Now.ToFileTimeUtc();
            var analysisRun = new AnalysisRunModel(analyzer, id)
            {
                Header = headerText
            };

            _analysisRuns.Add(analysisRun);

            return analysisRun;
        }

        internal void RemoveAnalysisRun(AnalysisRunModel analysisRun)
        {
            _analysisRuns.Remove(analysisRun);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void RaisePropertyChange([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
