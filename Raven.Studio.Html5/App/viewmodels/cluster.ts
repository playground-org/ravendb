import viewModelBase = require("viewmodels/viewModelBase");
import appUrl = require("common/appUrl");
import database = require("models/database");
import getClusterTopologyCommand = require("commands/getClusterTopologyCommand");
import messagePublisher = require("common/messagePublisher");
import topology = require("models/topology");
import nodeConnectionInfo = require("models/nodeConnectionInfo");
import editNodeConnectionInfoDialog = require("viewmodels/editNodeConnectionInfoDialog");
import app = require("durandal/app");
import getDatabaseStatsCommand = require("commands/getDatabaseStatsCommand");
import getStatusDebugConfigCommand = require("commands/getStatusDebugConfigCommand");
import extendRaftClusterCommand = require("commands/extendRaftClusterCommand");

class cluster extends viewModelBase {

    topology = ko.observable<topology>();
    systemDatabaseId = ko.observable<string>();
    serverUrl = ko.observable<string>();

    canActivate(args: any): JQueryPromise<any> {
        var deferred = $.Deferred();

        var db = appUrl.getSystemDatabase();
        $.when(this.fetchClusterTopology(db), this.fetchDatabaseId(db), this.fetchServerUrl(db))
            .done(() => deferred.resolve({ can: true }))
            .fail(() => deferred.resolve({ redirect: appUrl.forAdminSettings() }));
        return deferred;
    }

    fetchClusterTopology(db: database): JQueryPromise<any> {
        return new getClusterTopologyCommand(db)
            .execute()
            .done(topo => this.topology(topo))
            .fail(() => messagePublisher.reportError("Unable to fetch cluster topology"));
    }

    fetchDatabaseId(db: database): JQueryPromise<any> {
        return new getDatabaseStatsCommand(db)
            .execute()
            .done((stats: databaseStatisticsDto) => {
                this.systemDatabaseId(stats.DatabaseId);
            });
    }

    fetchServerUrl(db: database): JQueryPromise<any> {
        return new getStatusDebugConfigCommand(db)
            .execute()
            .done(config => this.serverUrl(config.ServerUrl));
    }

    addAnotherServerToCluster() {
        var newNode = nodeConnectionInfo.empty();
        var dialog = new editNodeConnectionInfoDialog(newNode);
        dialog
            .onExit()
            .done(nci => {
                new extendRaftClusterCommand(appUrl.getSystemDatabase(), nci.toDto(), false)
                    .execute()
                    .done(() => setTimeout(() => this.fetchClusterTopology(appUrl.getSystemDatabase()), 500));
        });
        app.showDialog(dialog);
    }

    createCluster() {
        var newNode = nodeConnectionInfo.empty();
        newNode.name(this.systemDatabaseId());
        newNode.uri(this.serverUrl());
        var dialog = new editNodeConnectionInfoDialog(newNode);
        dialog
            .onExit()
            .done(nci => {
            new extendRaftClusterCommand(appUrl.getSystemDatabase(), nci.toDto(), true)
                .execute()
                .done(() => setTimeout(() => this.fetchClusterTopology(appUrl.getSystemDatabase()), 500));

        });
        app.showDialog(dialog);

    }
}

export = cluster;