const TVHclientConfigurationPageVar = {
    pluginUniqueId: '3fd018e5-5e78-4e58-b280-a0c068febee0'
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

            // Absent means a configuration written before this setting existed, and the plugin
            // defaults it on, so the box has to agree rather than reading absence as "off".
            page.querySelector('#chkUseChannelLogoWhereArtworkIsMissing').checked =
                config.UseChannelLogoWhereArtworkIsMissing !== false;

            page.querySelector('#txtLiveBufferSizeMegabytes').value = config.LiveBufferSizeMegabytes || '512';
            Dashboard.hideLoadingMsg();
        });
    });

    view.querySelector('#btnResetArtwork').addEventListener('click', function () {
        Dashboard.showLoadingMsg();
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('TVHeadend/Artwork/Reset'),
            dataType: 'json'
        }).then(function (result) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Forgot the artwork of ' + result.Cleared + ' of ' + result.Total +
                ' recordings. It is fetched again the next time the recordings are listed.');
        }, function () {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('The recording artwork could not be reset. See the server log.');
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
            config.UseChannelLogoWhereArtworkIsMissing =
                form.querySelector('#chkUseChannelLogoWhereArtworkIsMissing').checked;

            config.HideRecordingsChannel = form.querySelector('#chkHideRecordingsChannel').checked;
            config.LiveBufferSizeMegabytes = form.querySelector('#txtLiveBufferSizeMegabytes').value;
            ApiClient.updatePluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId, config).then(Dashboard.processPluginConfigurationUpdateResult);
        });
        return false;
    });
}
