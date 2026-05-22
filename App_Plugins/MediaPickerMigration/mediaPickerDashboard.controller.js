angular.module("umbraco").controller("MediaPickerMigrationDashboardController", function ($scope, mpResource, notificationsService) {

    var vm = this;
    vm.loading = true;
    vm.auditResult = [];

    function loadAudit() {
        vm.loading = true;
        mpResource.getAll().then(function (response) {
            vm.auditResult = response || [];
            vm.loading = false;
        }).catch(function () {
            vm.auditResult = [];
            vm.loading = false;
        });
    }

    loadAudit();

    vm.fix = function (item) {
        item.isFixing = true;

        mpResource.fix({ dataTypeId: item.dataTypeId }).then(function (response) {
            notificationsService.success("Succes", "'" + item.dataTypeName + "' is bijgewerkt naar Umbraco.MediaPicker3.");
            item.isFixed = true;
            item.isFixing = false;
        }, function () {
            notificationsService.error("Fout", "Er ging iets mis bij het bijwerken van '" + item.dataTypeName + "'.");
            item.isFixing = false;
        });
    };
});
