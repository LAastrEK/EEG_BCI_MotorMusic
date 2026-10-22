namespace EventGenerator
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
            this.btnStartEvtClass1 = new System.Windows.Forms.Button();
            this.btnStartEvtClass2 = new System.Windows.Forms.Button();
            this.btnStartEvtClass3 = new System.Windows.Forms.Button();
            this.lblGain = new System.Windows.Forms.Label();
            this.numUpDwnDurationTime = new System.Windows.Forms.NumericUpDown();
            this.statusStripMain = new System.Windows.Forms.StatusStrip();
            this.toolStripStatusLabel1 = new System.Windows.Forms.ToolStripStatusLabel();
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnDurationTime)).BeginInit();
            this.statusStripMain.SuspendLayout();
            this.SuspendLayout();
            // 
            // btnStartEvtClass1
            // 
            this.btnStartEvtClass1.Location = new System.Drawing.Point(6, 12);
            this.btnStartEvtClass1.Name = "btnStartEvtClass1";
            this.btnStartEvtClass1.Size = new System.Drawing.Size(75, 23);
            this.btnStartEvtClass1.TabIndex = 0;
            this.btnStartEvtClass1.Text = "Evt Class 1";
            this.btnStartEvtClass1.UseVisualStyleBackColor = true;
            this.btnStartEvtClass1.Click += new System.EventHandler(this.btnStartEvtClass1_Click);
            // 
            // btnStartEvtClass2
            // 
            this.btnStartEvtClass2.Location = new System.Drawing.Point(100, 12);
            this.btnStartEvtClass2.Name = "btnStartEvtClass2";
            this.btnStartEvtClass2.Size = new System.Drawing.Size(75, 23);
            this.btnStartEvtClass2.TabIndex = 1;
            this.btnStartEvtClass2.Text = "Evt Class 2";
            this.btnStartEvtClass2.UseVisualStyleBackColor = true;
            this.btnStartEvtClass2.Click += new System.EventHandler(this.btnStartEvtClass2_Click);
            // 
            // btnStartEvtClass3
            // 
            this.btnStartEvtClass3.Location = new System.Drawing.Point(191, 12);
            this.btnStartEvtClass3.Name = "btnStartEvtClass3";
            this.btnStartEvtClass3.Size = new System.Drawing.Size(75, 23);
            this.btnStartEvtClass3.TabIndex = 2;
            this.btnStartEvtClass3.Text = "Evt Class 3";
            this.btnStartEvtClass3.UseVisualStyleBackColor = true;
            this.btnStartEvtClass3.Click += new System.EventHandler(this.btnStartEvtClass3_Click);
            // 
            // lblGain
            // 
            this.lblGain.AutoSize = true;
            this.lblGain.Location = new System.Drawing.Point(13, 72);
            this.lblGain.Name = "lblGain";
            this.lblGain.Size = new System.Drawing.Size(88, 13);
            this.lblGain.TabIndex = 32;
            this.lblGain.Text = "Evt Duration, ms:";
            // 
            // numUpDwnDurationTime
            // 
            this.numUpDwnDurationTime.Location = new System.Drawing.Point(107, 70);
            this.numUpDwnDurationTime.Maximum = new decimal(new int[] {
            1000000,
            0,
            0,
            0});
            this.numUpDwnDurationTime.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.numUpDwnDurationTime.Name = "numUpDwnDurationTime";
            this.numUpDwnDurationTime.Size = new System.Drawing.Size(78, 20);
            this.numUpDwnDurationTime.TabIndex = 31;
            this.numUpDwnDurationTime.Value = new decimal(new int[] {
            3000,
            0,
            0,
            0});
            // 
            // statusStripMain
            // 
            this.statusStripMain.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStripStatusLabel1});
            this.statusStripMain.Location = new System.Drawing.Point(0, 287);
            this.statusStripMain.Name = "statusStripMain";
            this.statusStripMain.Size = new System.Drawing.Size(484, 22);
            this.statusStripMain.TabIndex = 33;
            this.statusStripMain.Text = "statusStrip1";
            // 
            // toolStripStatusLabel1
            // 
            this.toolStripStatusLabel1.Name = "toolStripStatusLabel1";
            this.toolStripStatusLabel1.Size = new System.Drawing.Size(118, 17);
            this.toolStripStatusLabel1.Text = "toolStripStatusLabel1";
            // 
            // fmMain
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(484, 309);
            this.Controls.Add(this.statusStripMain);
            this.Controls.Add(this.lblGain);
            this.Controls.Add(this.numUpDwnDurationTime);
            this.Controls.Add(this.btnStartEvtClass3);
            this.Controls.Add(this.btnStartEvtClass2);
            this.Controls.Add(this.btnStartEvtClass1);
            this.Name = "fmMain";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Event Generator";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.fmMain_FormClosing);
            this.Load += new System.EventHandler(this.fmMain_Load);
            ((System.ComponentModel.ISupportInitialize)(this.numUpDwnDurationTime)).EndInit();
            this.statusStripMain.ResumeLayout(false);
            this.statusStripMain.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button btnStartEvtClass1;
        private System.Windows.Forms.Button btnStartEvtClass2;
        private System.Windows.Forms.Button btnStartEvtClass3;
        private System.Windows.Forms.Label lblGain;
        public System.Windows.Forms.NumericUpDown numUpDwnDurationTime;
        private System.Windows.Forms.StatusStrip statusStripMain;
        private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabel1;
    }
}

