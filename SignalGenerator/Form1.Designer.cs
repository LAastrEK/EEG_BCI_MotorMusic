namespace SignalGenerator
{
    partial class fmMain
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.statusStrpMain = new System.Windows.Forms.StatusStrip();
            this.toolStripStatusLabel1 = new System.Windows.Forms.ToolStripStatusLabel();
            this.tlStrpStatusSamplingRate = new System.Windows.Forms.ToolStripStatusLabel();
            this.toolStripStatusLabel3 = new System.Windows.Forms.ToolStripStatusLabel();
            this.spltCntnerMain = new System.Windows.Forms.SplitContainer();
            this.glCtrl = new OpenTK.GLControl();
            this.groupBox4 = new System.Windows.Forms.GroupBox();
            this.rtbDeviceInfo = new System.Windows.Forms.RichTextBox();
            this.groupBox3 = new System.Windows.Forms.GroupBox();
            this.cmbBxScaleAmpl = new System.Windows.Forms.ComboBox();
            this.label6 = new System.Windows.Forms.Label();
            this.label7 = new System.Windows.Forms.Label();
            this.label5 = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.numUpDwnTimeBaseInSec = new System.Windows.Forms.NumericUpDown();
            this.groupBox2 = new System.Windows.Forms.GroupBox();
            this.btnStartRecording = new System.Windows.Forms.Button();
            this.btnConfig = new System.Windows.Forms.Button();
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.lblSineFrequencies = new System.Windows.Forms.Label();
            this.pnlSineFrequencies = new System.Windows.Forms.Panel();
            this.label3 = new System.Windows.Forms.Label();
            this.cbWaveForm = new System.Windows.Forms.ComboBox();
            this.label2 = new System.Windows.Forms.Label();
            this.numUpDwnBlockSize = new System.Windows.Forms.NumericUpDown();
            this.label1 = new System.Windows.Forms.Label();
            this.numUpDwnSamplingRate = new System.Windows.Forms.NumericUpDown();
            this.lblGain = new System.Windows.Forms.Label();
            this.numUpDwnChnlsCnt = new System.Windows.Forms.NumericUpDown();
            this.btnStopGenerator = new System.Windows.Forms.Button();
            this.btnStartGenerator = new System.Windows.Forms.Button();
            this.statusStrpMain.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.spltCntnerMain)).BeginInit();
            this.spltCntnerMain.Panel1.SuspendLayout();
            this.spltCntnerMain.Panel2.SuspendLayout();
            this.spltCntnerMain.SuspendLayout();
            this.groupBox4.SuspendLayout();
            this.groupBox3.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnTimeBaseInSec)).BeginInit();
            this.groupBox2.SuspendLayout();
            this.groupBox1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnBlockSize)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnSamplingRate)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnChnlsCnt)).BeginInit();
            this.SuspendLayout();
            // 
            // statusStrpMain
            // 
            this.statusStrpMain.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStripStatusLabel1,
            this.tlStrpStatusSamplingRate,
            this.toolStripStatusLabel3});
            this.statusStrpMain.Location = new System.Drawing.Point(0, 667);
            this.statusStrpMain.Name = "statusStrpMain";
            this.statusStrpMain.Size = new System.Drawing.Size(1070, 22);
            this.statusStrpMain.TabIndex = 1;
            this.statusStrpMain.Text = "statusStrip1";
            // 
            // toolStripStatusLabel1
            // 
            this.toolStripStatusLabel1.Name = "toolStripStatusLabel1";
            this.toolStripStatusLabel1.Size = new System.Drawing.Size(125, 17);
            this.toolStripStatusLabel1.Text = "Signal Generator Sirius";
            // 
            // tlStrpStatusSamplingRate
            // 
            this.tlStrpStatusSamplingRate.Name = "tlStrpStatusSamplingRate";
            this.tlStrpStatusSamplingRate.Size = new System.Drawing.Size(42, 17);
            this.tlStrpStatusSamplingRate.Text = "500 Hz";
            // 
            // toolStripStatusLabel3
            // 
            this.toolStripStatusLabel3.Name = "toolStripStatusLabel3";
            this.toolStripStatusLabel3.Size = new System.Drawing.Size(86, 17);
            this.toolStripStatusLabel3.Text = "0 ch; REF None";
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
            this.spltCntnerMain.Panel1.Controls.Add(this.groupBox4);
            // 
            // spltCntnerMain.Panel2
            // 
            this.spltCntnerMain.Panel2.Controls.Add(this.groupBox3);
            this.spltCntnerMain.Panel2.Controls.Add(this.groupBox2);
            this.spltCntnerMain.Panel2.Controls.Add(this.btnConfig);
            this.spltCntnerMain.Panel2.Controls.Add(this.groupBox1);
            this.spltCntnerMain.Size = new System.Drawing.Size(1070, 667);
            this.spltCntnerMain.SplitterDistance = 768;
            this.spltCntnerMain.TabIndex = 2;
            // 
            // glCtrl
            // 
            this.glCtrl.BackColor = System.Drawing.Color.Black;
            this.glCtrl.Dock = System.Windows.Forms.DockStyle.Fill;
            this.glCtrl.Location = new System.Drawing.Point(0, 0);
            this.glCtrl.Name = "glCtrl";
            this.glCtrl.Size = new System.Drawing.Size(768, 445);
            this.glCtrl.TabIndex = 4;
            this.glCtrl.VSync = false;
            this.glCtrl.Paint += new System.Windows.Forms.PaintEventHandler(this.glCtrl_Paint);
            this.glCtrl.Resize += new System.EventHandler(this.glCtrl_Resize);
            // 
            // groupBox4
            // 
            this.groupBox4.Controls.Add(this.rtbDeviceInfo);
            this.groupBox4.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.groupBox4.Location = new System.Drawing.Point(0, 445);
            this.groupBox4.Name = "groupBox4";
            this.groupBox4.Size = new System.Drawing.Size(768, 222);
            this.groupBox4.TabIndex = 3;
            this.groupBox4.TabStop = false;
            this.groupBox4.Text = "Output";
            // 
            // rtbDeviceInfo
            // 
            this.rtbDeviceInfo.Dock = System.Windows.Forms.DockStyle.Fill;
            this.rtbDeviceInfo.Location = new System.Drawing.Point(3, 16);
            this.rtbDeviceInfo.Name = "rtbDeviceInfo";
            this.rtbDeviceInfo.Size = new System.Drawing.Size(762, 203);
            this.rtbDeviceInfo.TabIndex = 3;
            this.rtbDeviceInfo.Text = "";
            // 
            // groupBox3
            // 
            this.groupBox3.Controls.Add(this.cmbBxScaleAmpl);
            this.groupBox3.Controls.Add(this.label6);
            this.groupBox3.Controls.Add(this.label7);
            this.groupBox3.Controls.Add(this.label5);
            this.groupBox3.Controls.Add(this.label4);
            this.groupBox3.Controls.Add(this.numUpDwnTimeBaseInSec);
            this.groupBox3.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.groupBox3.Location = new System.Drawing.Point(0, 486);
            this.groupBox3.Name = "groupBox3";
            this.groupBox3.Size = new System.Drawing.Size(298, 123);
            this.groupBox3.TabIndex = 2;
            this.groupBox3.TabStop = false;
            this.groupBox3.Text = "Display";
            // 
            // cmbBxScaleAmpl
            // 
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
            this.cmbBxScaleAmpl.Location = new System.Drawing.Point(105, 54);
            this.cmbBxScaleAmpl.Name = "cmbBxScaleAmpl";
            this.cmbBxScaleAmpl.Size = new System.Drawing.Size(78, 21);
            this.cmbBxScaleAmpl.TabIndex = 37;
            this.cmbBxScaleAmpl.SelectedIndexChanged += new System.EventHandler(this.cmbBxScaleAmpl_SelectedIndexChanged);
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(206, 57);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(31, 13);
            this.label6.TabIndex = 36;
            this.label6.Text = "/ Div";
            // 
            // label7
            // 
            this.label7.AutoSize = true;
            this.label7.Location = new System.Drawing.Point(18, 57);
            this.label7.Name = "label7";
            this.label7.Size = new System.Drawing.Size(63, 13);
            this.label7.TabIndex = 35;
            this.label7.Text = "Scale Ampl:";
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(206, 31);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(40, 13);
            this.label5.TabIndex = 33;
            this.label5.Text = "/ Page";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(18, 31);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(79, 13);
            this.label4.TabIndex = 32;
            this.label4.Text = "Timebase, sec:";
            // 
            // numUpDwnTimeBaseInSec
            // 
            this.numUpDwnTimeBaseInSec.Location = new System.Drawing.Point(105, 29);
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
            this.numUpDwnTimeBaseInSec.Size = new System.Drawing.Size(78, 20);
            this.numUpDwnTimeBaseInSec.TabIndex = 31;
            this.numUpDwnTimeBaseInSec.Value = new decimal(new int[] {
            5,
            0,
            0,
            0});
            this.numUpDwnTimeBaseInSec.ValueChanged += new System.EventHandler(this.numUpDwnTimeBaseInSec_ValueChanged);
            // 
            // groupBox2
            // 
            this.groupBox2.Controls.Add(this.btnStartRecording);
            this.groupBox2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.groupBox2.Location = new System.Drawing.Point(0, 341);
            this.groupBox2.Name = "groupBox2";
            this.groupBox2.Size = new System.Drawing.Size(298, 268);
            this.groupBox2.TabIndex = 1;
            this.groupBox2.TabStop = false;
            this.groupBox2.Text = "Data Storage";
            // 
            // btnStartRecording
            // 
            this.btnStartRecording.Location = new System.Drawing.Point(9, 66);
            this.btnStartRecording.Name = "btnStartRecording";
            this.btnStartRecording.Size = new System.Drawing.Size(275, 54);
            this.btnStartRecording.TabIndex = 0;
            this.btnStartRecording.Text = "Srart Recording";
            this.btnStartRecording.UseVisualStyleBackColor = true;
            // 
            // btnConfig
            // 
            this.btnConfig.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.btnConfig.Location = new System.Drawing.Point(0, 609);
            this.btnConfig.Name = "btnConfig";
            this.btnConfig.Size = new System.Drawing.Size(298, 58);
            this.btnConfig.TabIndex = 3;
            this.btnConfig.Text = "Configuration...";
            this.btnConfig.UseVisualStyleBackColor = true;
            // 
            // groupBox1
            // 
            this.groupBox1.Controls.Add(this.lblSineFrequencies);
            this.groupBox1.Controls.Add(this.pnlSineFrequencies);
            this.groupBox1.Controls.Add(this.label3);
            this.groupBox1.Controls.Add(this.cbWaveForm);
            this.groupBox1.Controls.Add(this.label2);
            this.groupBox1.Controls.Add(this.numUpDwnBlockSize);
            this.groupBox1.Controls.Add(this.label1);
            this.groupBox1.Controls.Add(this.numUpDwnSamplingRate);
            this.groupBox1.Controls.Add(this.lblGain);
            this.groupBox1.Controls.Add(this.numUpDwnChnlsCnt);
            this.groupBox1.Controls.Add(this.btnStopGenerator);
            this.groupBox1.Controls.Add(this.btnStartGenerator);
            this.groupBox1.Dock = System.Windows.Forms.DockStyle.Top;
            this.groupBox1.Location = new System.Drawing.Point(0, 0);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Size = new System.Drawing.Size(298, 341);
            this.groupBox1.TabIndex = 0;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = "Generator";
            // 
            // lblSineFrequencies
            // 
            this.lblSineFrequencies.AutoSize = true;
            this.lblSineFrequencies.Location = new System.Drawing.Point(18, 180);
            this.lblSineFrequencies.Name = "lblSineFrequencies";
            this.lblSineFrequencies.Size = new System.Drawing.Size(188, 13);
            this.lblSineFrequencies.TabIndex = 37;
            this.lblSineFrequencies.Text = "Sine frequencies, Hz (0.1 - 100.0)";
            // 
            // pnlSineFrequencies
            // 
            this.pnlSineFrequencies.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left) | System.Windows.Forms.AnchorStyles.Right)));
            this.pnlSineFrequencies.AutoScroll = true;
            this.pnlSineFrequencies.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.pnlSineFrequencies.Location = new System.Drawing.Point(18, 200);
            this.pnlSineFrequencies.Name = "pnlSineFrequencies";
            this.pnlSineFrequencies.Size = new System.Drawing.Size(266, 130);
            this.pnlSineFrequencies.TabIndex = 38;
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(18, 151);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(62, 13);
            this.label3.TabIndex = 36;
            this.label3.Text = "WaveForm:";
            // 
            // cbWaveForm
            // 
            this.cbWaveForm.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbWaveForm.FormattingEnabled = true;
            this.cbWaveForm.Location = new System.Drawing.Point(105, 148);
            this.cbWaveForm.Name = "cbWaveForm";
            this.cbWaveForm.Size = new System.Drawing.Size(123, 21);
            this.cbWaveForm.TabIndex = 35;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(18, 124);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(60, 13);
            this.label2.TabIndex = 34;
            this.label2.Text = "Block Size:";
            // 
            // numUpDwnBlockSize
            // 
            this.numUpDwnBlockSize.Location = new System.Drawing.Point(105, 122);
            this.numUpDwnBlockSize.Maximum = new decimal(new int[] {
            1000,
            0,
            0,
            0});
            this.numUpDwnBlockSize.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.numUpDwnBlockSize.Name = "numUpDwnBlockSize";
            this.numUpDwnBlockSize.Size = new System.Drawing.Size(78, 20);
            this.numUpDwnBlockSize.TabIndex = 33;
            this.numUpDwnBlockSize.Value = new decimal(new int[] {
            3,
            0,
            0,
            0});
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(18, 98);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(79, 13);
            this.label1.TabIndex = 32;
            this.label1.Text = "Sampling Rate:";
            // 
            // numUpDwnSamplingRate
            // 
            this.numUpDwnSamplingRate.Location = new System.Drawing.Point(105, 96);
            this.numUpDwnSamplingRate.Maximum = new decimal(new int[] {
            10000,
            0,
            0,
            0});
            this.numUpDwnSamplingRate.Minimum = new decimal(new int[] {
            50,
            0,
            0,
            0});
            this.numUpDwnSamplingRate.Name = "numUpDwnSamplingRate";
            this.numUpDwnSamplingRate.Size = new System.Drawing.Size(78, 20);
            this.numUpDwnSamplingRate.TabIndex = 31;
            this.numUpDwnSamplingRate.Value = new decimal(new int[] {
            500,
            0,
            0,
            0});
            // 
            // lblGain
            // 
            this.lblGain.AutoSize = true;
            this.lblGain.Location = new System.Drawing.Point(18, 72);
            this.lblGain.Name = "lblGain";
            this.lblGain.Size = new System.Drawing.Size(54, 13);
            this.lblGain.TabIndex = 30;
            this.lblGain.Text = "Channels:";
            // 
            // numUpDwnChnlsCnt
            // 
            this.numUpDwnChnlsCnt.Location = new System.Drawing.Point(105, 70);
            this.numUpDwnChnlsCnt.Maximum = new decimal(new int[] {
            160,
            0,
            0,
            0});
            this.numUpDwnChnlsCnt.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.numUpDwnChnlsCnt.Name = "numUpDwnChnlsCnt";
            this.numUpDwnChnlsCnt.Size = new System.Drawing.Size(78, 20);
            this.numUpDwnChnlsCnt.TabIndex = 29;
            this.numUpDwnChnlsCnt.Value = new decimal(new int[] {
            5,
            0,
            0,
            0});
            // 
            // btnStopGenerator
            // 
            this.btnStopGenerator.Location = new System.Drawing.Point(154, 19);
            this.btnStopGenerator.Name = "btnStopGenerator";
            this.btnStopGenerator.Size = new System.Drawing.Size(138, 39);
            this.btnStopGenerator.TabIndex = 4;
            this.btnStopGenerator.Text = "Stop";
            this.btnStopGenerator.UseVisualStyleBackColor = true;
            this.btnStopGenerator.Click += new System.EventHandler(this.btnStopGenerator_Click);
            // 
            // btnStartGenerator
            // 
            this.btnStartGenerator.Location = new System.Drawing.Point(9, 19);
            this.btnStartGenerator.Name = "btnStartGenerator";
            this.btnStartGenerator.Size = new System.Drawing.Size(126, 39);
            this.btnStartGenerator.TabIndex = 0;
            this.btnStartGenerator.Text = "Start";
            this.btnStartGenerator.UseVisualStyleBackColor = true;
            this.btnStartGenerator.Click += new System.EventHandler(this.btnStartGenerator_Click);
            // 
            // fmMain
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1070, 689);
            this.Controls.Add(this.spltCntnerMain);
            this.Controls.Add(this.statusStrpMain);
            this.Name = "fmMain";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "SignalGenerator";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.fmMain_FormClosing);
            this.Load += new System.EventHandler(this.fmMain_Load);
            this.statusStrpMain.ResumeLayout(false);
            this.statusStrpMain.PerformLayout();
            this.spltCntnerMain.Panel1.ResumeLayout(false);
            this.spltCntnerMain.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.spltCntnerMain)).EndInit();
            this.spltCntnerMain.ResumeLayout(false);
            this.groupBox4.ResumeLayout(false);
            this.groupBox3.ResumeLayout(false);
            this.groupBox3.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnTimeBaseInSec)).EndInit();
            this.groupBox2.ResumeLayout(false);
            this.groupBox1.ResumeLayout(false);
            this.groupBox1.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnBlockSize)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnSamplingRate)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnChnlsCnt)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.StatusStrip statusStrpMain;
        private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabel1;
        private System.Windows.Forms.ToolStripStatusLabel tlStrpStatusSamplingRate;
        private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabel3;
        private System.Windows.Forms.SplitContainer spltCntnerMain;
        private OpenTK.GLControl glCtrl;
        private System.Windows.Forms.GroupBox groupBox4;
        private System.Windows.Forms.RichTextBox rtbDeviceInfo;
        private System.Windows.Forms.GroupBox groupBox3;
        private System.Windows.Forms.GroupBox groupBox2;
        private System.Windows.Forms.Button btnStartRecording;
        private System.Windows.Forms.Button btnConfig;
        private System.Windows.Forms.GroupBox groupBox1;
        private System.Windows.Forms.Button btnStopGenerator;
        private System.Windows.Forms.Button btnStartGenerator;
        private System.Windows.Forms.Label lblGain;
        public System.Windows.Forms.NumericUpDown numUpDwnChnlsCnt;
        private System.Windows.Forms.Label label1;
        public System.Windows.Forms.NumericUpDown numUpDwnSamplingRate;
        private System.Windows.Forms.Label label3;
        public System.Windows.Forms.ComboBox cbWaveForm;
        private System.Windows.Forms.Label lblSineFrequencies;
        private System.Windows.Forms.Panel pnlSineFrequencies;
        private System.Windows.Forms.Label label2;
        public System.Windows.Forms.NumericUpDown numUpDwnBlockSize;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.Label label4;
        public System.Windows.Forms.NumericUpDown numUpDwnTimeBaseInSec;
        private System.Windows.Forms.ComboBox cmbBxScaleAmpl;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.Label label7;
    }
}

