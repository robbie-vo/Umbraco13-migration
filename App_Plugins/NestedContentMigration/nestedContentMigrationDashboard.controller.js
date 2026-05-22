angular.module("umbraco").controller("NestedContentMigrationDashboardController", function (userService, logResource, entityResource, ncResource, notificationsService) {

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
                    if (item.logType !== "Save" && item.logType !== "Publish" && item.logType !== "SavePublish") {
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
            }, function (error) {
                console.error("Fout bij laden van logs:", error);
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
    }).catch(function () {
        vm.loading = false;
    });

    vm.mpAuditResult = [];
    vm.mpLoading = true;
    vm.mpFixing = false;

    vm.loadMediaPickerAudit = function () {
        vm.mpLoading = true;
        ncResource.mediaPickerAudit().then(function (response) {
            vm.mpAuditResult = response || [];
            vm.mpLoading = false;
        }).catch(function () {
            vm.mpLoading = false;
        });
    };

    vm.loadMediaPickerAudit();

    vm.fixMediaPickers = function () {
        vm.mpFixing = true;
        ncResource.mediaPickerFix().then(function (response) {
            notificationsService.success("Succes", response.message);
            vm.loadMediaPickerAudit();
            vm.mpFixing = false;
        }).catch(function () {
            notificationsService.error("Fout", "Media picker migratie mislukt.");
            vm.mpFixing = false;
        });
    };

    vm.schemaChecks = [];
    vm.schemaLoading = true;
    vm.schemaFixing = false;

    vm.loadSchemaChecks = function () {
        vm.schemaLoading = true;
        ncResource.schemaCheck().then(function (response) {
            vm.schemaChecks = response || [];
            vm.schemaLoading = false;
        }).catch(function () {
            vm.schemaLoading = false;
        });
    };

    vm.loadSchemaChecks();

    vm.fixSchema = function () {
        vm.schemaFixing = true;
        ncResource.schemaFix().then(function (response) {
            var count = (response.fixed_ || []).length;
            if (count > 0) {
                notificationsService.success("Schema gerepareerd", count + " kolom(men) toegevoegd.");
            } else {
                notificationsService.info("Niets te repareren", "Alle kolommen zijn al aanwezig.");
            }
            vm.loadSchemaChecks();
            vm.schemaFixing = false;
        }).catch(function () {
            notificationsService.error("Fout", "Schema reparatie mislukt.");
            vm.schemaFixing = false;
        });
    };

    vm.checkMigration = function (item) {
        var blAlias = item.duplicateAlias || item.newAlias;
        if (!blAlias || blAlias.trim() === '') {
            notificationsService.error("Validatie fout", "Geen BlockList alias beschikbaar. Voer de migratie eerst uit of voer een alias in.");
            return;
        }

        item.isChecking = true;
        item.checkResults = null;

        ncResource.contentCheck(item.docTypeAlias, item.propertyAlias, blAlias).then(function (response) {
            item.checkResults = response || [];
            item.isChecking = false;
        }).catch(function () {
            notificationsService.error("Fout", "Content check mislukt.");
            item.isChecking = false;
        });
    };

    vm.updateProperty = function (item) {
        // Validatie: check of er een nieuwe alias is ingevoerd
        if (!item.newAlias || item.newAlias.trim() === '') {
            notificationsService.error("Validatie fout", "Voer eerst een nieuwe alias in.");
            return;
        }

        // Set loading state voor deze specifieke button
        item.isUpdating = true;

        var postData = {
            docTypeAlias: item.docTypeAlias,
            propertyAlias: item.propertyAlias,
            newAlias: item.newAlias
        };

        ncResource.update(postData).then(function (response) {
            if (response && response.errorCount > 0) {
                notificationsService.warning("Migratie voltooid met fouten", response.message + " Controleer het Umbraco logboek voor details.");
            } else {
                notificationsService.success("Succes", response ? response.message : "De property is bijgewerkt.");
            }
            vm.loadUserLogs();

            ncResource.getAll().then(function (auditResponse) {
                vm.AuditResult = auditResponse || [];
            }, function () {
                vm.AuditResult = [];
            });

            item.isUpdating = false;
        }, function () {
            notificationsService.error("Fout", "Er ging iets mis bij het updaten.");
            item.isUpdating = false;
        });
    };
});