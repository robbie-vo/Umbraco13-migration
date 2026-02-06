angular.module("umbraco").controller("NestedContentMigrationDashboardController", function ($scope, userService, logResource, entityResource, ncResource, notificationsService) {

    var vm = this;
    vm.UserName = "guest";
    vm.AuditResult = [];
    vm.UserLogHistory = [];

    var userLogOptions = {
        pageSize: 20,
        pageNumber: 1,
        orderDirection: "Descending",
        sinceDate: new Date(2018, 0, 1)
    };

    vm.loadUserLogs = function () {
        logResource.getPagedUserLog(userLogOptions)
            .then(function (response) {
                console.log("Logs geladen:", response);
                vm.UserLogHistory = response;

                var supportedEntityTypes = ["Document", "Media"];
                var nodesWeKnowAbout = [];
                var filteredLogEntries = [];

                angular.forEach(response.items, function (item) {
                    if (nodesWeKnowAbout.includes(item.nodeId) || !supportedEntityTypes.includes(item.entityType)) {
                        return;
                    }
                    if (item.logType !== "Save" && item.logType !== "Publish") {
                        return;
                    }
                    if (item.nodeId < 0) {
                        return;
                    }

                    nodesWeKnowAbout.push(item.nodeId);

                    entityResource.getById(item.nodeId, item.entityType).then(function (ent) {
                        item.Content = ent;
                    });

                    if (item.entityType === "Document") {
                        item.editUrl = "content/content/edit/" + item.nodeId;
                    }
                    if (item.entityType === "Media") {
                        item.editUrl = "media/media/edit/" + item.nodeId;
                    }

                    filteredLogEntries.push(item);
                });

                vm.UserLogHistory.items = filteredLogEntries;
            });
    };

    vm.loadUserLogs();

    userService.getCurrentUser().then(function (user) {
        vm.UserName = user.name;
    });

    vm.loading = true;
    ncResource.getAll().then(function (response) {
        vm.AuditResult = response || [];
        vm.loading = false;
    }).catch(function (error) {
        vm.loading = false;
    });

    vm.updateProperty = function (item) {
        // Set loading state voor deze specifieke button
        item.isUpdating = true;

        var postData = {
            docTypeAlias: item.docTypeAlias,
            propertyAlias: item.propertyAlias,
            newAlias: item.newAlias
        };

        ncResource.update(postData).then(function (response) {
            notificationsService.success("Succes", "De property is bijgewerkt.");
            vm.loadUserLogs();
            // Verwijder loading state na succesvolle update
            item.isUpdating = false;
        }, function (error) {
            notificationsService.error("Fout", "Er ging iets mis bij het updaten.");
            // Verwijder loading state ook bij een fout
            item.isUpdating = false;
        });
    };
});