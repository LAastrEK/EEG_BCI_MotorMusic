namespace ActiChamp
{
    partial class fmMain
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.statusStrpMain = new System.Windows.Forms.StatusStrip();
            this.toolStripStatusLabelTitle = new System.Windows.Forms.ToolStripStatusLabel();
            this.tlStrpStatusSamplingRate = new System.Windows.Forms.ToolStripStatusLabel();
            this.toolStripStatusLabelChannels = new System.Windows.Forms.ToolStripStatusLabel();
            this.toolStripStatusDropped = new System.Windows.Forms.ToolStripStatusLabel();
            this.spltCntnerMain = new System.Windows.Forms.SplitContainer();
            this.glCtrl = new OpenTK.GLControl();
            this.groupBoxOutput = new System.Windows.Forms.GroupBox();
            this.rtbDeviceInfo = new System.Windows.Forms.RichTextBox();
            this.groupBoxChannels = new System.Windows.Forms.GroupBox();
            this.dgvChannels = new System.Windows.Forms.DataGridView();
            this.panelChannelsBottom = new System.Windows.Forms.Panel();
            this.lblChannelsSelected = new System.Windows.Forms.Label();
            this.btnChannelsNone = new System.Windows.Forms.Button();
            this.btnChannelsAll = new System.Windows.Forms.Button();
            this.groupBoxDisplay = new System.Windows.Forms.GroupBox();
            this.btnBaselineNow = new System.Windows.Forms.Button();
            this.labelBaselineCorrection = new System.Windows.Forms.Label();
            this.cmbBxScaleAmpl = new System.Windows.Forms.ComboBox();
            this.labelScaleDiv = new System.Windows.Forms.Label();
            this.labelScaleAmpl = new System.Windows.Forms.Label();
            this.labelPage = new System.Windows.Forms.Label();
            this.labelTimeBase = new System.Windows.Forms.Label();
            this.numUpDwnTimeBaseInSec = new System.Windows.Forms.NumericUpDown();
            this.groupBoxFilters = new System.Windows.Forms.GroupBox();
            this.chkNotch50Hz = new System.Windows.Forms.CheckBox();
            this.groupBoxEogFilters = new System.Windows.Forms.GroupBox();
            this.cmbEogHighCutoff = new System.Windows.Forms.ComboBox();
            this.cmbEogLowCutoff = new System.Windows.Forms.ComboBox();
            this.labelEogHighCutoff = new System.Windows.Forms.Label();
            this.labelEogLowCutoff = new System.Windows.Forms.Label();
            this.groupBoxEegFilters = new System.Windows.Forms.GroupBox();
            this.cmbHighCutoff = new System.Windows.Forms.ComboBox();
            this.cmbLowCutoff = new System.Windows.Forms.ComboBox();
            this.labelHighCutoff = new System.Windows.Forms.Label();
            this.labelLowCutoff = new System.Windows.Forms.Label();
            this.groupBoxDevice = new System.Windows.Forms.GroupBox();
            this.lblDeviceState = new System.Windows.Forms.Label();
            this.labelDeviceStateCaption = new System.Windows.Forms.Label();
            this.numUpDwnActiveShieldGain = new System.Windows.Forms.NumericUpDown();
            this.labelActiveShieldGain = new System.Windows.Forms.Label();
            this.numUpDwnBlockSize = new System.Windows.Forms.NumericUpDown();
            this.labelBlockSize = new System.Windows.Forms.Label();
            this.cmbSamplingRate = new System.Windows.Forms.ComboBox();
            this.labelSamplingRate = new System.Windows.Forms.Label();
            this.btnImpedance = new System.Windows.Forms.Button();
            this.btnStopAcquisition = new System.Windows.Forms.Button();
            this.btnStartAcquisition = new System.Windows.Forms.Button();
            this.btnInitialize = new System.Windows.Forms.Button();
            this.colChannelUse = new System.Windows.Forms.DataGridViewCheckBoxColumn();
            this.colChannelRef = new System.Windows.Forms.DataGridViewCheckBoxColumn();
            this.colChannelPhysical = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colChannelName = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.colChannelType = new System.Windows.Forms.DataGridViewComboBoxColumn();
            this.statusStrpMain.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.spltCntnerMain)).BeginInit();
            this.spltCntnerMain.Panel1.SuspendLayout();
            this.spltCntnerMain.Panel2.SuspendLayout();
            this.spltCntnerMain.SuspendLayout();
            this.groupBoxOutput.SuspendLayout();
            this.groupBoxChannels.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvChannels)).BeginInit();
            this.panelChannelsBottom.SuspendLayout();
            this.groupBoxDisplay.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnTimeBaseInSec)).BeginInit();
            this.groupBoxFilters.SuspendLayout();
            this.groupBoxEogFilters.SuspendLayout();
            this.groupBoxEegFilters.SuspendLayout();
            this.groupBoxDevice.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnActiveShieldGain)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnBlockSize)).BeginInit();
            this.SuspendLayout();
            // 
            // statusStrpMain
            // 
            this.statusStrpMain.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStripStatusLabelTitle,
            this.tlStrpStatusSamplingRate,
            this.toolStripStatusLabelChannels,
            this.toolStripStatusDropped});
            this.statusStrpMain.Location = new System.Drawing.Point(0, 698);
            this.statusStrpMain.Name = "statusStrpMain";
            this.statusStrpMain.Size = new System.Drawing.Size(1120, 22);
            this.statusStrpMain.TabIndex = 0;
            // 
            // toolStripStatusLabelTitle
            // 
            this.toolStripStatusLabelTitle.Name = "toolStripStatusLabelTitle";
            this.toolStripStatusLabelTitle.Size = new System.Drawing.Size(124, 17);
            this.toolStripStatusLabelTitle.Text = "actiCHamp / PolyHub";
            // 
            // tlStrpStatusSamplingRate
            // 
            this.tlStrpStatusSamplingRate.Name = "tlStrpStatusSamplingRate";
            this.tlStrpStatusSamplingRate.Size = new System.Drawing.Size(42, 17);
            this.tlStrpStatusSamplingRate.Text = "500 Hz";
            // 
            // toolStripStatusLabelChannels
            // 
            this.toolStripStatusLabelChannels.Name = "toolStripStatusLabelChannels";
            this.toolStripStatusLabelChannels.Size = new System.Drawing.Size(82, 17);
            this.toolStripStatusLabelChannels.Text = "0 EEG + 0 AUX";
            // 
            // toolStripStatusDropped
            // 
            this.toolStripStatusDropped.Name = "toolStripStatusDropped";
            this.toolStripStatusDropped.Size = new System.Drawing.Size(102, 17);
            this.toolStripStatusDropped.Text = "Dropped blocks: 0";
            // 
            // spltCntnerMain
            // 
            this.spltCntnerMain.Dock = System.Windows.Forms.DockStyle.Fill;
            this.spltCntnerMain.Location = new System.Drawing.Point(0, 0);
            this.spltCntnerMain.Name = "spltCntnerMain";
            // 
            // spltCntnerMain.Panel1
            // 
            this.spltCntnerMain.Panel1.Controls.Add(this.glCtrl);
            this.spltCntnerMain.Panel1.Controls.Add(this.groupBoxOutput);
            // 
            // spltCntnerMain.Panel2
            // 
            this.spltCntnerMain.Panel2.Controls.Add(this.groupBoxChannels);
            this.spltCntnerMain.Panel2.Controls.Add(this.groupBoxDisplay);
            this.spltCntnerMain.Panel2.Controls.Add(this.groupBoxFilters);
            this.spltCntnerMain.Panel2.Controls.Add(this.groupBoxDevice);
            this.spltCntnerMain.Size = new System.Drawing.Size(1120, 698);
            this.spltCntnerMain.SplitterDistance = 819;
            this.spltCntnerMain.TabIndex = 1;
            // 
            // glCtrl
            // 
            this.glCtrl.BackColor = System.Drawing.Color.Black;
            this.glCtrl.Dock = System.Windows.Forms.DockStyle.Fill;
            this.glCtrl.Location = new System.Drawing.Point(0, 0);
            this.glCtrl.Name = "glCtrl";
            this.glCtrl.Size = new System.Drawing.Size(819, 476);
            this.glCtrl.TabIndex = 0;
            this.glCtrl.VSync = false;
            this.glCtrl.Paint += new System.Windows.Forms.PaintEventHandler(this.glCtrl_Paint);
            this.glCtrl.Resize += new System.EventHandler(this.glCtrl_Resize);
            // 
            // groupBoxOutput
            // 
            this.groupBoxOutput.Controls.Add(this.rtbDeviceInfo);
            this.groupBoxOutput.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.groupBoxOutput.Location = new System.Drawing.Point(0, 476);
            this.groupBoxOutput.Name = "groupBoxOutput";
            this.groupBoxOutput.Size = new System.Drawing.Size(819, 222);
            this.groupBoxOutput.TabIndex = 1;
            this.groupBoxOutput.TabStop = false;
            this.groupBoxOutput.Text = "Output";
            // 
            // rtbDeviceInfo
            // 
            this.rtbDeviceInfo.Dock = System.Windows.Forms.DockStyle.Fill;
            this.rtbDeviceInfo.Location = new System.Drawing.Point(3, 16);
            this.rtbDeviceInfo.Name = "rtbDeviceInfo";
            this.rtbDeviceInfo.ReadOnly = true;
            this.rtbDeviceInfo.Size = new System.Drawing.Size(813, 203);
            this.rtbDeviceInfo.TabIndex = 0;
            this.rtbDeviceInfo.Text = "";
            // 
            // groupBoxChannels
            // 
            this.groupBoxChannels.Controls.Add(this.dgvChannels);
            this.groupBoxChannels.Controls.Add(this.panelChannelsBottom);
            this.groupBoxChannels.Dock = System.Windows.Forms.DockStyle.Fill;
            this.groupBoxChannels.Location = new System.Drawing.Point(0, 508);
            this.groupBoxChannels.Name = "groupBoxChannels";
            this.groupBoxChannels.Size = new System.Drawing.Size(297, 190);
            this.groupBoxChannels.TabIndex = 3;
            this.groupBoxChannels.TabStop = false;
            this.groupBoxChannels.Text = "Channels";
            // 
            // dgvChannels
            // 
            this.dgvChannels.AllowUserToAddRows = false;
            this.dgvChannels.AllowUserToDeleteRows = false;
            this.dgvChannels.AllowUserToResizeRows = false;
            this.dgvChannels.BackgroundColor = System.Drawing.SystemColors.Window;
            this.dgvChannels.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.dgvChannels.CellBorderStyle = System.Windows.Forms.DataGridViewCellBorderStyle.SingleHorizontal;
            this.dgvChannels.ColumnHeadersBorderStyle = System.Windows.Forms.DataGridViewHeaderBorderStyle.Single;
            this.dgvChannels.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvChannels.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.colChannelUse,
            this.colChannelRef,
            this.colChannelPhysical,
            this.colChannelName,
            this.colChannelType});
            this.dgvChannels.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvChannels.EditMode = System.Windows.Forms.DataGridViewEditMode.EditOnEnter;
            this.dgvChannels.Enabled = false;
            this.dgvChannels.Location = new System.Drawing.Point(3, 16);
            this.dgvChannels.MultiSelect = false;
            this.dgvChannels.Name = "dgvChannels";
            this.dgvChannels.RowHeadersVisible = false;
            this.dgvChannels.RowTemplate.Height = 20;
            this.dgvChannels.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
            this.dgvChannels.Size = new System.Drawing.Size(291, 167);
            this.dgvChannels.TabIndex = 0;
            this.dgvChannels.CellBeginEdit += new System.Windows.Forms.DataGridViewCellCancelEventHandler(this.dgvChannels_CellBeginEdit);
            this.dgvChannels.CellValueChanged += new System.Windows.Forms.DataGridViewCellEventHandler(this.dgvChannels_CellValueChanged);
            this.dgvChannels.CurrentCellDirtyStateChanged += new System.EventHandler(this.dgvChannels_CurrentCellDirtyStateChanged);
            // 
            // panelChannelsBottom
            // 
            this.panelChannelsBottom.Controls.Add(this.lblChannelsSelected);
            this.panelChannelsBottom.Controls.Add(this.btnChannelsNone);
            this.panelChannelsBottom.Controls.Add(this.btnChannelsAll);
            this.panelChannelsBottom.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.panelChannelsBottom.Location = new System.Drawing.Point(3, 183);
            this.panelChannelsBottom.Name = "panelChannelsBottom";
            this.panelChannelsBottom.Size = new System.Drawing.Size(291, 34);
            this.panelChannelsBottom.TabIndex = 1;
            // 
            // lblChannelsSelected
            // 
            this.lblChannelsSelected.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.lblChannelsSelected.Location = new System.Drawing.Point(126, 9);
            this.lblChannelsSelected.Name = "lblChannelsSelected";
            this.lblChannelsSelected.Size = new System.Drawing.Size(156, 15);
            this.lblChannelsSelected.TabIndex = 2;
            this.lblChannelsSelected.Text = "0 selected";
            this.lblChannelsSelected.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // btnChannelsNone
            // 
            this.btnChannelsNone.Enabled = false;
            this.btnChannelsNone.Location = new System.Drawing.Point(65, 5);
            this.btnChannelsNone.Name = "btnChannelsNone";
            this.btnChannelsNone.Size = new System.Drawing.Size(54, 23);
            this.btnChannelsNone.TabIndex = 1;
            this.btnChannelsNone.Text = "None";
            this.btnChannelsNone.UseVisualStyleBackColor = true;
            this.btnChannelsNone.Click += new System.EventHandler(this.btnChannelsNone_Click);
            // 
            // btnChannelsAll
            // 
            this.btnChannelsAll.Enabled = false;
            this.btnChannelsAll.Location = new System.Drawing.Point(5, 5);
            this.btnChannelsAll.Name = "btnChannelsAll";
            this.btnChannelsAll.Size = new System.Drawing.Size(54, 23);
            this.btnChannelsAll.TabIndex = 0;
            this.btnChannelsAll.Text = "All";
            this.btnChannelsAll.UseVisualStyleBackColor = true;
            this.btnChannelsAll.Click += new System.EventHandler(this.btnChannelsAll_Click);
            // 
            // groupBoxDisplay
            // 
            this.groupBoxDisplay.Controls.Add(this.btnBaselineNow);
            this.groupBoxDisplay.Controls.Add(this.labelBaselineCorrection);
            this.groupBoxDisplay.Controls.Add(this.cmbBxScaleAmpl);
            this.groupBoxDisplay.Controls.Add(this.labelScaleDiv);
            this.groupBoxDisplay.Controls.Add(this.labelScaleAmpl);
            this.groupBoxDisplay.Controls.Add(this.labelPage);
            this.groupBoxDisplay.Controls.Add(this.labelTimeBase);
            this.groupBoxDisplay.Controls.Add(this.numUpDwnTimeBaseInSec);
            this.groupBoxDisplay.Dock = System.Windows.Forms.DockStyle.Top;
            this.groupBoxDisplay.Location = new System.Drawing.Point(0, 385);
            this.groupBoxDisplay.Name = "groupBoxDisplay";
            this.groupBoxDisplay.Size = new System.Drawing.Size(297, 123);
            this.groupBoxDisplay.TabIndex = 2;
            this.groupBoxDisplay.TabStop = false;
            this.groupBoxDisplay.Text = "Display";
            // 
            // btnBaselineNow
            // 
            this.btnBaselineNow.Enabled = false;
            this.btnBaselineNow.Location = new System.Drawing.Point(112, 85);
            this.btnBaselineNow.Name = "btnBaselineNow";
            this.btnBaselineNow.Size = new System.Drawing.Size(82, 24);
            this.btnBaselineNow.TabIndex = 7;
            this.btnBaselineNow.Text = "Now";
            this.btnBaselineNow.UseVisualStyleBackColor = true;
            this.btnBaselineNow.Click += new System.EventHandler(this.btnBaselineNow_Click);
            // 
            // labelBaselineCorrection
            // 
            this.labelBaselineCorrection.Location = new System.Drawing.Point(18, 82);
            this.labelBaselineCorrection.Name = "labelBaselineCorrection";
            this.labelBaselineCorrection.Size = new System.Drawing.Size(79, 30);
            this.labelBaselineCorrection.TabIndex = 6;
            this.labelBaselineCorrection.Text = "Baseline\r\ncorrection:";
            this.labelBaselineCorrection.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // cmbBxScaleAmpl
            // 
            this.cmbBxScaleAmpl.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbBxScaleAmpl.FormattingEnabled = true;
            this.cmbBxScaleAmpl.Items.AddRange(new object[] {
            "0.5 µV",
            "1 µV",
            "2 µV",
            "5 µV",
            "10 µV",
            "20 µV",
            "50 µV",
            "100 µV",
            "200 µV",
            "500 µV",
            "1 mV",
            "2 mV",
            "5 mV",
            "10 mV",
            "20 mV",
            "50 mV",
            "100 mV",
            "200 mV",
            "500 mV",
            "1 V",
            "2 V",
            "5 V"});
            this.cmbBxScaleAmpl.Location = new System.Drawing.Point(112, 55);
            this.cmbBxScaleAmpl.Name = "cmbBxScaleAmpl";
            this.cmbBxScaleAmpl.Size = new System.Drawing.Size(82, 21);
            this.cmbBxScaleAmpl.TabIndex = 2;
            this.cmbBxScaleAmpl.SelectedIndexChanged += new System.EventHandler(this.cmbBxScaleAmpl_SelectedIndexChanged);
            // 
            // labelScaleDiv
            // 
            this.labelScaleDiv.AutoSize = true;
            this.labelScaleDiv.Location = new System.Drawing.Point(205, 58);
            this.labelScaleDiv.Name = "labelScaleDiv";
            this.labelScaleDiv.Size = new System.Drawing.Size(31, 13);
            this.labelScaleDiv.TabIndex = 5;
            this.labelScaleDiv.Text = "/ Div";
            // 
            // labelScaleAmpl
            // 
            this.labelScaleAmpl.AutoSize = true;
            this.labelScaleAmpl.Location = new System.Drawing.Point(18, 58);
            this.labelScaleAmpl.Name = "labelScaleAmpl";
            this.labelScaleAmpl.Size = new System.Drawing.Size(63, 13);
            this.labelScaleAmpl.TabIndex = 4;
            this.labelScaleAmpl.Text = "Scale Ampl:";
            // 
            // labelPage
            // 
            this.labelPage.AutoSize = true;
            this.labelPage.Location = new System.Drawing.Point(205, 32);
            this.labelPage.Name = "labelPage";
            this.labelPage.Size = new System.Drawing.Size(40, 13);
            this.labelPage.TabIndex = 3;
            this.labelPage.Text = "/ Page";
            // 
            // labelTimeBase
            // 
            this.labelTimeBase.AutoSize = true;
            this.labelTimeBase.Location = new System.Drawing.Point(18, 32);
            this.labelTimeBase.Name = "labelTimeBase";
            this.labelTimeBase.Size = new System.Drawing.Size(79, 13);
            this.labelTimeBase.TabIndex = 2;
            this.labelTimeBase.Text = "Timebase, sec:";
            // 
            // numUpDwnTimeBaseInSec
            // 
            this.numUpDwnTimeBaseInSec.Location = new System.Drawing.Point(112, 29);
            this.numUpDwnTimeBaseInSec.Maximum = new decimal(new int[] {
            160,
            0,
            0,
            0});
            this.numUpDwnTimeBaseInSec.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.numUpDwnTimeBaseInSec.Name = "numUpDwnTimeBaseInSec";
            this.numUpDwnTimeBaseInSec.Size = new System.Drawing.Size(82, 20);
            this.numUpDwnTimeBaseInSec.TabIndex = 1;
            this.numUpDwnTimeBaseInSec.Value = new decimal(new int[] {
            5,
            0,
            0,
            0});
            this.numUpDwnTimeBaseInSec.ValueChanged += new System.EventHandler(this.numUpDwnTimeBaseInSec_ValueChanged);
            // 
            // groupBoxFilters
            // 
            this.groupBoxFilters.Controls.Add(this.chkNotch50Hz);
            this.groupBoxFilters.Controls.Add(this.groupBoxEogFilters);
            this.groupBoxFilters.Controls.Add(this.groupBoxEegFilters);
            this.groupBoxFilters.Dock = System.Windows.Forms.DockStyle.Top;
            this.groupBoxFilters.Location = new System.Drawing.Point(0, 260);
            this.groupBoxFilters.Name = "groupBoxFilters";
            this.groupBoxFilters.Size = new System.Drawing.Size(297, 125);
            this.groupBoxFilters.TabIndex = 1;
            this.groupBoxFilters.TabStop = false;
            this.groupBoxFilters.Text = "Filters";
            // 
            // chkNotch50Hz
            // 
            this.chkNotch50Hz.AutoSize = true;
            this.chkNotch50Hz.Location = new System.Drawing.Point(105, 102);
            this.chkNotch50Hz.Name = "chkNotch50Hz";
            this.chkNotch50Hz.Size = new System.Drawing.Size(86, 17);
            this.chkNotch50Hz.TabIndex = 2;
            this.chkNotch50Hz.Text = "Notch 50 Hz";
            this.chkNotch50Hz.UseVisualStyleBackColor = true;
            // 
            // groupBoxEogFilters
            // 
            this.groupBoxEogFilters.Controls.Add(this.cmbEogHighCutoff);
            this.groupBoxEogFilters.Controls.Add(this.cmbEogLowCutoff);
            this.groupBoxEogFilters.Controls.Add(this.labelEogHighCutoff);
            this.groupBoxEogFilters.Controls.Add(this.labelEogLowCutoff);
            this.groupBoxEogFilters.Location = new System.Drawing.Point(151, 18);
            this.groupBoxEogFilters.Name = "groupBoxEogFilters";
            this.groupBoxEogFilters.Size = new System.Drawing.Size(138, 78);
            this.groupBoxEogFilters.TabIndex = 1;
            this.groupBoxEogFilters.TabStop = false;
            this.groupBoxEogFilters.Text = "EOG";
            // 
            // cmbEogHighCutoff
            // 
            this.cmbEogHighCutoff.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbEogHighCutoff.FormattingEnabled = true;
            this.cmbEogHighCutoff.Items.AddRange(new object[] {
            "Off",
            "15 Hz",
            "20 Hz",
            "30 Hz",
            "40 Hz",
            "50 Hz",
            "100 Hz"});
            this.cmbEogHighCutoff.Location = new System.Drawing.Point(55, 44);
            this.cmbEogHighCutoff.Name = "cmbEogHighCutoff";
            this.cmbEogHighCutoff.Size = new System.Drawing.Size(72, 21);
            this.cmbEogHighCutoff.TabIndex = 3;
            // 
            // cmbEogLowCutoff
            // 
            this.cmbEogLowCutoff.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbEogLowCutoff.FormattingEnabled = true;
            this.cmbEogLowCutoff.Items.AddRange(new object[] {
            "Off",
            "0.01 Hz",
            "0.05 Hz",
            "0.1 Hz",
            "0.5 Hz",
            "1 Hz"});
            this.cmbEogLowCutoff.Location = new System.Drawing.Point(55, 18);
            this.cmbEogLowCutoff.Name = "cmbEogLowCutoff";
            this.cmbEogLowCutoff.Size = new System.Drawing.Size(72, 21);
            this.cmbEogLowCutoff.TabIndex = 1;
            // 
            // labelEogHighCutoff
            // 
            this.labelEogHighCutoff.AutoSize = true;
            this.labelEogHighCutoff.Location = new System.Drawing.Point(10, 47);
            this.labelEogHighCutoff.Name = "labelEogHighCutoff";
            this.labelEogHighCutoff.Size = new System.Drawing.Size(32, 13);
            this.labelEogHighCutoff.TabIndex = 2;
            this.labelEogHighCutoff.Text = "High:";
            // 
            // labelEogLowCutoff
            // 
            this.labelEogLowCutoff.AutoSize = true;
            this.labelEogLowCutoff.Location = new System.Drawing.Point(10, 21);
            this.labelEogLowCutoff.Name = "labelEogLowCutoff";
            this.labelEogLowCutoff.Size = new System.Drawing.Size(30, 13);
            this.labelEogLowCutoff.TabIndex = 0;
            this.labelEogLowCutoff.Text = "Low:";
            // 
            // groupBoxEegFilters
            // 
            this.groupBoxEegFilters.Controls.Add(this.cmbHighCutoff);
            this.groupBoxEegFilters.Controls.Add(this.cmbLowCutoff);
            this.groupBoxEegFilters.Controls.Add(this.labelHighCutoff);
            this.groupBoxEegFilters.Controls.Add(this.labelLowCutoff);
            this.groupBoxEegFilters.Location = new System.Drawing.Point(8, 18);
            this.groupBoxEegFilters.Name = "groupBoxEegFilters";
            this.groupBoxEegFilters.Size = new System.Drawing.Size(136, 78);
            this.groupBoxEegFilters.TabIndex = 0;
            this.groupBoxEegFilters.TabStop = false;
            this.groupBoxEegFilters.Text = "EEG";
            // 
            // cmbHighCutoff
            // 
            this.cmbHighCutoff.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbHighCutoff.FormattingEnabled = true;
            this.cmbHighCutoff.Items.AddRange(new object[] {
            "Off",
            "10 Hz",
            "20 Hz",
            "30 Hz",
            "50 Hz",
            "100 Hz",
            "200 Hz",
            "500 Hz",
            "1000 Hz",
            "2000 Hz"});
            this.cmbHighCutoff.Location = new System.Drawing.Point(55, 44);
            this.cmbHighCutoff.Name = "cmbHighCutoff";
            this.cmbHighCutoff.Size = new System.Drawing.Size(72, 21);
            this.cmbHighCutoff.TabIndex = 3;
            // 
            // cmbLowCutoff
            // 
            this.cmbLowCutoff.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbLowCutoff.FormattingEnabled = true;
            this.cmbLowCutoff.Items.AddRange(new object[] {
            "Off",
            "0.01 Hz",
            "0.02 Hz",
            "0.05 Hz",
            "0.1 Hz",
            "0.2 Hz",
            "0.5 Hz",
            "1 Hz",
            "2 Hz",
            "5 Hz",
            "10 Hz"});
            this.cmbLowCutoff.Location = new System.Drawing.Point(55, 18);
            this.cmbLowCutoff.Name = "cmbLowCutoff";
            this.cmbLowCutoff.Size = new System.Drawing.Size(72, 21);
            this.cmbLowCutoff.TabIndex = 1;
            // 
            // labelHighCutoff
            // 
            this.labelHighCutoff.AutoSize = true;
            this.labelHighCutoff.Location = new System.Drawing.Point(10, 47);
            this.labelHighCutoff.Name = "labelHighCutoff";
            this.labelHighCutoff.Size = new System.Drawing.Size(32, 13);
            this.labelHighCutoff.TabIndex = 2;
            this.labelHighCutoff.Text = "High:";
            // 
            // labelLowCutoff
            // 
            this.labelLowCutoff.AutoSize = true;
            this.labelLowCutoff.Location = new System.Drawing.Point(10, 21);
            this.labelLowCutoff.Name = "labelLowCutoff";
            this.labelLowCutoff.Size = new System.Drawing.Size(30, 13);
            this.labelLowCutoff.TabIndex = 0;
            this.labelLowCutoff.Text = "Low:";
            // groupBoxDevice
            // 
            this.groupBoxDevice.Controls.Add(this.lblDeviceState);
            this.groupBoxDevice.Controls.Add(this.labelDeviceStateCaption);
            this.groupBoxDevice.Controls.Add(this.numUpDwnActiveShieldGain);
            this.groupBoxDevice.Controls.Add(this.labelActiveShieldGain);
            this.groupBoxDevice.Controls.Add(this.numUpDwnBlockSize);
            this.groupBoxDevice.Controls.Add(this.labelBlockSize);
            this.groupBoxDevice.Controls.Add(this.cmbSamplingRate);
            this.groupBoxDevice.Controls.Add(this.labelSamplingRate);
            this.groupBoxDevice.Controls.Add(this.btnImpedance);
            this.groupBoxDevice.Controls.Add(this.btnStopAcquisition);
            this.groupBoxDevice.Controls.Add(this.btnStartAcquisition);
            this.groupBoxDevice.Controls.Add(this.btnInitialize);
            this.groupBoxDevice.Dock = System.Windows.Forms.DockStyle.Top;
            this.groupBoxDevice.Location = new System.Drawing.Point(0, 0);
            this.groupBoxDevice.Name = "groupBoxDevice";
            this.groupBoxDevice.Size = new System.Drawing.Size(297, 260);
            this.groupBoxDevice.TabIndex = 0;
            this.groupBoxDevice.TabStop = false;
            this.groupBoxDevice.Text = "actiCHamp";
            // 
            // lblDeviceState
            // 
            this.lblDeviceState.AutoSize = true;
            this.lblDeviceState.Location = new System.Drawing.Point(112, 222);
            this.lblDeviceState.Name = "lblDeviceState";
            this.lblDeviceState.Size = new System.Drawing.Size(69, 13);
            this.lblDeviceState.TabIndex = 10;
            this.lblDeviceState.Text = "Not initialized";
            // 
            // labelDeviceStateCaption
            // 
            this.labelDeviceStateCaption.AutoSize = true;
            this.labelDeviceStateCaption.Location = new System.Drawing.Point(18, 222);
            this.labelDeviceStateCaption.Name = "labelDeviceStateCaption";
            this.labelDeviceStateCaption.Size = new System.Drawing.Size(35, 13);
            this.labelDeviceStateCaption.TabIndex = 9;
            this.labelDeviceStateCaption.Text = "State:";
            // 
            // numUpDwnActiveShieldGain
            // 
            this.numUpDwnActiveShieldGain.Location = new System.Drawing.Point(112, 183);
            this.numUpDwnActiveShieldGain.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.numUpDwnActiveShieldGain.Name = "numUpDwnActiveShieldGain";
            this.numUpDwnActiveShieldGain.Size = new System.Drawing.Size(82, 20);
            this.numUpDwnActiveShieldGain.TabIndex = 8;
            this.numUpDwnActiveShieldGain.Value = new decimal(new int[] {
            5,
            0,
            0,
            0});
            // 
            // labelActiveShieldGain
            // 
            this.labelActiveShieldGain.AutoSize = true;
            this.labelActiveShieldGain.Location = new System.Drawing.Point(18, 185);
            this.labelActiveShieldGain.Name = "labelActiveShieldGain";
            this.labelActiveShieldGain.Size = new System.Drawing.Size(64, 13);
            this.labelActiveShieldGain.TabIndex = 7;
            this.labelActiveShieldGain.Text = "Shield Gain:";
            // 
            // numUpDwnBlockSize
            // 
            this.numUpDwnBlockSize.Location = new System.Drawing.Point(112, 157);
            this.numUpDwnBlockSize.Maximum = new decimal(new int[] {
            10000,
            0,
            0,
            0});
            this.numUpDwnBlockSize.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.numUpDwnBlockSize.Name = "numUpDwnBlockSize";
            this.numUpDwnBlockSize.Size = new System.Drawing.Size(82, 20);
            this.numUpDwnBlockSize.TabIndex = 6;
            this.numUpDwnBlockSize.Value = new decimal(new int[] {
            32,
            0,
            0,
            0});
            // 
            // labelBlockSize
            // 
            this.labelBlockSize.AutoSize = true;
            this.labelBlockSize.Location = new System.Drawing.Point(18, 159);
            this.labelBlockSize.Name = "labelBlockSize";
            this.labelBlockSize.Size = new System.Drawing.Size(60, 13);
            this.labelBlockSize.TabIndex = 5;
            this.labelBlockSize.Text = "Block Size:";
            // 
            // cmbSamplingRate
            // 
            this.cmbSamplingRate.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbSamplingRate.FormattingEnabled = true;
            this.cmbSamplingRate.Location = new System.Drawing.Point(112, 130);
            this.cmbSamplingRate.Name = "cmbSamplingRate";
            this.cmbSamplingRate.Size = new System.Drawing.Size(110, 21);
            this.cmbSamplingRate.TabIndex = 4;
            // 
            // labelSamplingRate
            // 
            this.labelSamplingRate.AutoSize = true;
            this.labelSamplingRate.Location = new System.Drawing.Point(18, 133);
            this.labelSamplingRate.Name = "labelSamplingRate";
            this.labelSamplingRate.Size = new System.Drawing.Size(79, 13);
            this.labelSamplingRate.TabIndex = 3;
            this.labelSamplingRate.Text = "Sampling Rate:";
            // 
            // btnImpedance
            // 
            this.btnImpedance.Enabled = false;
            this.btnImpedance.Location = new System.Drawing.Point(199, 76);
            this.btnImpedance.Name = "btnImpedance";
            this.btnImpedance.Size = new System.Drawing.Size(84, 39);
            this.btnImpedance.TabIndex = 3;
            this.btnImpedance.Text = "Impedance";
            this.btnImpedance.UseVisualStyleBackColor = true;
            this.btnImpedance.Click += new System.EventHandler(this.btnImpedance_Click);
            // 
            // btnStopAcquisition
            // 
            this.btnStopAcquisition.Location = new System.Drawing.Point(104, 76);
            this.btnStopAcquisition.Name = "btnStopAcquisition";
            this.btnStopAcquisition.Size = new System.Drawing.Size(88, 39);
            this.btnStopAcquisition.TabIndex = 2;
            this.btnStopAcquisition.Text = "Stop";
            this.btnStopAcquisition.UseVisualStyleBackColor = true;
            this.btnStopAcquisition.Click += new System.EventHandler(this.btnStopAcquisition_Click);
            // 
            // btnStartAcquisition
            // 
            this.btnStartAcquisition.Location = new System.Drawing.Point(9, 76);
            this.btnStartAcquisition.Name = "btnStartAcquisition";
            this.btnStartAcquisition.Size = new System.Drawing.Size(88, 39);
            this.btnStartAcquisition.TabIndex = 1;
            this.btnStartAcquisition.Text = "Start";
            this.btnStartAcquisition.UseVisualStyleBackColor = true;
            this.btnStartAcquisition.Click += new System.EventHandler(this.btnStartAcquisition_Click);
            // 
            // btnInitialize
            // 
            this.btnInitialize.Location = new System.Drawing.Point(9, 22);
            this.btnInitialize.Name = "btnInitialize";
            this.btnInitialize.Size = new System.Drawing.Size(274, 39);
            this.btnInitialize.TabIndex = 0;
            this.btnInitialize.Text = "Initialize actiCHamp";
            this.btnInitialize.UseVisualStyleBackColor = true;
            this.btnInitialize.Click += new System.EventHandler(this.btnInitialize_Click);
            // 
            // colChannelUse
            // 
            this.colChannelUse.HeaderText = "Use";
            this.colChannelUse.Name = "colChannelUse";
            this.colChannelUse.Resizable = System.Windows.Forms.DataGridViewTriState.False;
            this.colChannelUse.Width = 42;
            // 
            // colChannelRef
            // 
            this.colChannelRef.HeaderText = "Ref";
            this.colChannelRef.Name = "colChannelRef";
            this.colChannelRef.Resizable = System.Windows.Forms.DataGridViewTriState.False;
            this.colChannelRef.Width = 42;
            // 
            // colChannelPhysical
            // 
            this.colChannelPhysical.HeaderText = "Channel";
            this.colChannelPhysical.Name = "colChannelPhysical";
            this.colChannelPhysical.ReadOnly = true;
            this.colChannelPhysical.Width = 64;
            // 
            // colChannelName
            // 
            this.colChannelName.AutoSizeMode = System.Windows.Forms.DataGridViewAutoSizeColumnMode.Fill;
            this.colChannelName.HeaderText = "Name";
            this.colChannelName.MaxInputLength = 31;
            this.colChannelName.Name = "colChannelName";
            // 
            // colChannelType
            // 
            this.colChannelType.HeaderText = "Type";
            this.colChannelType.Items.AddRange(new object[] {
            "EEG",
            "HEOG-L",
            "HEOG-R",
            "VEOG-U",
            "VEOG-D",
            "AUX"});
            this.colChannelType.Name = "colChannelType";
            this.colChannelType.Resizable = System.Windows.Forms.DataGridViewTriState.False;
            this.colChannelType.Width = 72;
            // 
            // fmMain
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1120, 720);
            this.Controls.Add(this.spltCntnerMain);
            this.Controls.Add(this.statusStrpMain);
            this.Name = "fmMain";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "actiCHamp";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.fmMain_FormClosing);
            this.Load += new System.EventHandler(this.fmMain_Load);
            this.statusStrpMain.ResumeLayout(false);
            this.statusStrpMain.PerformLayout();
            this.spltCntnerMain.Panel1.ResumeLayout(false);
            this.spltCntnerMain.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.spltCntnerMain)).EndInit();
            this.spltCntnerMain.ResumeLayout(false);
            this.groupBoxOutput.ResumeLayout(false);
            this.groupBoxChannels.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvChannels)).EndInit();
            this.panelChannelsBottom.ResumeLayout(false);
            this.groupBoxDisplay.ResumeLayout(false);
            this.groupBoxDisplay.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnTimeBaseInSec)).EndInit();
            this.groupBoxFilters.ResumeLayout(false);
            this.groupBoxFilters.PerformLayout();
            this.groupBoxEogFilters.ResumeLayout(false);
            this.groupBoxEogFilters.PerformLayout();
            this.groupBoxEegFilters.ResumeLayout(false);
            this.groupBoxEegFilters.PerformLayout();
            this.groupBoxDevice.ResumeLayout(false);
            this.groupBoxDevice.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnActiveShieldGain)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnBlockSize)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.StatusStrip statusStrpMain;
        private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabelTitle;
        private System.Windows.Forms.ToolStripStatusLabel tlStrpStatusSamplingRate;
        private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabelChannels;
        private System.Windows.Forms.ToolStripStatusLabel toolStripStatusDropped;
        private System.Windows.Forms.SplitContainer spltCntnerMain;
        private OpenTK.GLControl glCtrl;
        private System.Windows.Forms.GroupBox groupBoxOutput;
        private System.Windows.Forms.RichTextBox rtbDeviceInfo;
        private System.Windows.Forms.GroupBox groupBoxChannels;
        private System.Windows.Forms.DataGridView dgvChannels;
        private System.Windows.Forms.Panel panelChannelsBottom;
        private System.Windows.Forms.Label lblChannelsSelected;
        private System.Windows.Forms.Button btnChannelsNone;
        private System.Windows.Forms.Button btnChannelsAll;
        private System.Windows.Forms.GroupBox groupBoxFilters;
        private System.Windows.Forms.CheckBox chkNotch50Hz;
        private System.Windows.Forms.GroupBox groupBoxEogFilters;
        private System.Windows.Forms.ComboBox cmbEogHighCutoff;
        private System.Windows.Forms.ComboBox cmbEogLowCutoff;
        private System.Windows.Forms.Label labelEogHighCutoff;
        private System.Windows.Forms.Label labelEogLowCutoff;
        private System.Windows.Forms.GroupBox groupBoxEegFilters;
        private System.Windows.Forms.ComboBox cmbHighCutoff;
        private System.Windows.Forms.ComboBox cmbLowCutoff;
        private System.Windows.Forms.Label labelHighCutoff;
        private System.Windows.Forms.Label labelLowCutoff;
        private System.Windows.Forms.GroupBox groupBoxDisplay;
        private System.Windows.Forms.Label labelBaselineCorrection;
        private System.Windows.Forms.Button btnBaselineNow;
        private System.Windows.Forms.ComboBox cmbBxScaleAmpl;
        private System.Windows.Forms.Label labelScaleDiv;
        private System.Windows.Forms.Label labelScaleAmpl;
        private System.Windows.Forms.Label labelPage;
        private System.Windows.Forms.Label labelTimeBase;
        private System.Windows.Forms.NumericUpDown numUpDwnTimeBaseInSec;
        private System.Windows.Forms.GroupBox groupBoxDevice;
        private System.Windows.Forms.Label lblDeviceState;
        private System.Windows.Forms.Label labelDeviceStateCaption;
        private System.Windows.Forms.NumericUpDown numUpDwnActiveShieldGain;
        private System.Windows.Forms.Label labelActiveShieldGain;
        private System.Windows.Forms.NumericUpDown numUpDwnBlockSize;
        private System.Windows.Forms.Label labelBlockSize;
        private System.Windows.Forms.ComboBox cmbSamplingRate;
        private System.Windows.Forms.Label labelSamplingRate;
        private System.Windows.Forms.Button btnStopAcquisition;
        private System.Windows.Forms.Button btnStartAcquisition;
        private System.Windows.Forms.Button btnImpedance;
        private System.Windows.Forms.Button btnInitialize;
        private System.Windows.Forms.DataGridViewCheckBoxColumn colChannelUse;
        private System.Windows.Forms.DataGridViewCheckBoxColumn colChannelRef;
        private System.Windows.Forms.DataGridViewTextBoxColumn colChannelPhysical;
        private System.Windows.Forms.DataGridViewTextBoxColumn colChannelName;
        private System.Windows.Forms.DataGridViewComboBoxColumn colChannelType;
    }
}
