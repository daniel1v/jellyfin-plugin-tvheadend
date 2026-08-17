const TVHclientConfigurationPageVar = {
    pluginUniqueId: '3fd018e5-5e78-4e58-b280-a0c068febee0',

    // What the plugin has been able to establish about each stream profile role. Only the
    // server knows this: whether TVHeadend reports a profile of that name, and whether an
    // opened stream of that role actually delivered what the role promises.
    showStreamProfileStatus: function (page) {
        const target = page.querySelector('#streamProfileStatus');
        if (!target) {
            return;
        }

        ApiClient.getJSON(ApiClient.getUrl('TVHeadend/StreamProfiles')).then(function (roles) {
            const rows = roles.map(function (role) {
                const name = role.ProfileName ? role.ProfileName : 'not configured';
                const detail = role.Detail ? ' &mdash; ' + role.Detail : '';
                return '<div>' + role.Role + ': <strong>' + name + '</strong> (' + role.State + ')' + detail + '</div>';
            });
            target.innerHTML = '<div class="fieldDescription">' + rows.join('') + '</div>';
        }, function () {
            target.innerHTML = '<div class="fieldDescription">Profile status is available once the plugin has connected to TVHeadend.</div>';
        });
    }
};

export default function (view, params) {
    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        const page = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(function(config) {
            page.querySelector('#txtTVH_ServerName').value = config.TVH_ServerName || '';
            page.querySelector('#txtHTTP_Port').value = config.HTTP_Port || '9981';
            page.querySelector('#txtHTSP_Port').value = config.HTSP_Port || '9982';
            page.querySelector('#txtUserName').value = config.Username || '';
            page.querySelector('#txtPassword').value = config.Password || '';
            page.querySelector('#txtPriority').value = config.Priority || '5';
            page.querySelector('#txtDvrProfile').value = config.DvrProfile || '';
            page.querySelector('#txtPrePadding').value = config.Pre_Padding || '0';
            page.querySelector('#txtPostPadding').value = config.Post_Padding || '0';
            page.querySelector('#selChannelType').value = config.ChannelType || 'Ignore';
            page.querySelector('#chkHideRecordingsChannel').checked = config.HideRecordingsChannel || false;
            page.querySelector('#chkEnableSubsMaudios').checked = config.EnableSubsMaudios || false;
            page.querySelector('#chkAnalyzeChannelFormatsOnRefresh').checked = config.AnalyzeChannelFormatsOnRefresh || false;
            page.querySelector('#txtNativeStreamProfile').value = config.NativeStreamProfile || 'pass';
            page.querySelector('#txtMpeg2H264CompatibilityProfile').value = config.Mpeg2H264CompatibilityProfile || '';
            page.querySelector('#txtH264IdrNormalizationProfile').value = config.H264IdrNormalizationProfile || '';
            page.querySelector('#txtLiveBufferSizeMegabytes').value = config.LiveBufferSizeMegabytes || '512';
            Dashboard.hideLoadingMsg();
        });
        TVHclientConfigurationPageVar.showStreamProfileStatus(page);
    });

    view.querySelector('#btnReanalyze').addEventListener('click', function () {
        Dashboard.showLoadingMsg();
        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('TVHeadend/Channels/Reanalyze') }).then(function (count) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Discarded ' + count + ' channel description(s). They are established again on the next refresh or playback.');
            TVHclientConfigurationPageVar.showStreamProfileStatus(view);
        }, function () {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Could not discard the channel descriptions.');
        });
    });
    view.querySelector('.TVHclientConfigurationForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        const form = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(function(config) {
            config.TVH_ServerName = form.querySelector('#txtTVH_ServerName').value;
            config.HTTP_Port = form.querySelector('#txtHTTP_Port').value;
            config.HTSP_Port = form.querySelector('#txtHTSP_Port').value;
            config.Username = form.querySelector('#txtUserName').value;
            config.Password = form.querySelector('#txtPassword').value;
            config.Priority = form.querySelector('#txtPriority').value;
            config.DvrProfile = form.querySelector('#txtDvrProfile').value;
            config.Pre_Padding = form.querySelector('#txtPrePadding').value;
            config.Post_Padding = form.querySelector('#txtPostPadding').value;
            config.ChannelType = form.querySelector('#selChannelType').value;
            config.HideRecordingsChannel = form.querySelector('#chkHideRecordingsChannel').checked;
            config.EnableSubsMaudios = form.querySelector('#chkEnableSubsMaudios').checked;
            config.AnalyzeChannelFormatsOnRefresh = form.querySelector('#chkAnalyzeChannelFormatsOnRefresh').checked;
            config.NativeStreamProfile = form.querySelector('#txtNativeStreamProfile').value;
            config.Mpeg2H264CompatibilityProfile = form.querySelector('#txtMpeg2H264CompatibilityProfile').value;
            config.H264IdrNormalizationProfile = form.querySelector('#txtH264IdrNormalizationProfile').value;
            config.LiveBufferSizeMegabytes = form.querySelector('#txtLiveBufferSizeMegabytes').value;
            ApiClient.updatePluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId, config).then(Dashboard.processPluginConfigurationUpdateResult);
        });
        return false;
    });
}
